namespace Tamma.Core.Documents.Types;

/// <summary>
/// The per-document-type violation code vocabulary the shared
/// <see cref="DependencyGraphCheck"/> emits. Each document type that carries a
/// dependency graph (Story 39-3 <c>Decomposition</c>, Story 39-4 <c>Plan</c>)
/// supplies its OWN SCREAMING_SNAKE_CASE code strings so the shared checker keeps
/// one implementation while every type keeps its own vocabulary (Design Decision
/// D10 — codes are platform vocabulary).
/// </summary>
internal readonly record struct DependencyGraphCodes(
    string DuplicateId,
    string DanglingDependency,
    string SelfDependency,
    string Cyclic,
    string NoPrerequisiteOrder);

/// <summary>
/// The single, shared dependency-graph checker (Story 39-3 step 2, Design
/// Decision D10). Extracted UP FRONT so Story 39-4's <c>Plan</c> type reuses ONE
/// copy of the cycle / dangling / self / duplicate / topological-order checks
/// rather than minting a second implementation.
///
/// <para>
/// The checks are pure over an <c>(id, dependsOn)</c> node list and return
/// domain-phrased <see cref="DocumentViolation"/>s (never bare schema paths) so
/// 39-9's repair ring can feed the message back to the model. The canonical
/// example is the cycle path — <c>"Cyclic dependsOn: ST-2 -> ST-4 -> ST-2"</c>
/// names the actual members.
/// </para>
///
/// <para>
/// Acyclicity ⟺ a topological order exists (Kahn's algorithm is the constructive
/// proof). Rather than run Kahn separately, the DFS back-edge detection below is
/// the equivalent decision procedure; when it finds a cycle it emits BOTH the
/// naming <see cref="DependencyGraphCodes.Cyclic"/> code AND the stable
/// <see cref="DependencyGraphCodes.NoPrerequisiteOrder"/> signal (D10) that
/// downstream sequencing consumers (Stories 2-15/2-16) key on.
/// </para>
/// </summary>
internal static class DependencyGraphCheck
{
    /// <summary>
    /// Run the graph checks over <paramref name="nodes"/>, returning every
    /// violation found (empty when the graph is a clean DAG with unique ids).
    /// Nodes with an empty id are the caller's responsibility to flag (they are
    /// not graph vertices); this checker only considers non-empty ids.
    /// </summary>
    internal static List<DocumentViolation> Check(
        IReadOnlyList<(string Id, IReadOnlyList<string> DependsOn)> nodes,
        DependencyGraphCodes codes)
    {
        var violations = new List<DocumentViolation>();

        // ---- 1. duplicate ids (baseline kept-first — now loud) ------------------
        var idCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, _) in nodes)
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;
            idCounts.TryGetValue(id, out var count);
            idCounts[id] = count + 1;
        }

        foreach (var (id, count) in idCounts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (count > 1)
                violations.Add(new DocumentViolation(
                    codes.DuplicateId,
                    $"Duplicate task id '{id}' — task ids must be unique so dependencies are unambiguous."));
        }

        var idSet = new HashSet<string>(idCounts.Keys, StringComparer.Ordinal);

        // Build the adjacency from the FIRST occurrence of each id (mirrors the
        // baseline's keep-first behaviour) so a duplicate does not double the edges.
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var (id, dependsOn) in nodes)
        {
            if (string.IsNullOrWhiteSpace(id) || adjacency.ContainsKey(id))
                continue;
            adjacency[id] = dependsOn?.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).ToList()
                            ?? new List<string>();
            order.Add(id);
        }

        // ---- 2. self / dangling edges (baseline pruned — now loud) --------------
        var reportedSelf = new HashSet<string>(StringComparer.Ordinal);
        var reportedDangling = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in order)
        {
            foreach (var dep in adjacency[id])
            {
                if (string.Equals(dep, id, StringComparison.Ordinal))
                {
                    if (reportedSelf.Add(id))
                        violations.Add(new DocumentViolation(
                            codes.SelfDependency,
                            $"Task '{id}' depends on itself — a task cannot be its own prerequisite."));
                }
                else if (!idSet.Contains(dep))
                {
                    if (reportedDangling.Add($"{id}->{dep}"))
                        violations.Add(new DocumentViolation(
                            codes.DanglingDependency,
                            $"Task '{id}' depends on '{dep}', which is not a task in this document."));
                }
            }
        }

        // ---- 3. cycles over the CLEAN graph (self/dangling excluded) ------------
        // Only real edges (both endpoints present, not self) can form a cycle, so
        // self-loops and dangling refs above never manufacture a phantom cycle.
        var clean = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var id in order)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var edges = new List<string>();
            foreach (var dep in adjacency[id])
            {
                if (string.Equals(dep, id, StringComparison.Ordinal)) continue;
                if (!idSet.Contains(dep)) continue;
                if (!seen.Add(dep)) continue;
                edges.Add(dep);
            }
            clean[id] = edges;
        }

        var cyclePath = FindCycle(order, clean);
        if (cyclePath is not null)
        {
            violations.Add(new DocumentViolation(
                codes.Cyclic,
                $"Cyclic dependsOn: {string.Join(" -> ", cyclePath)}"));
            violations.Add(new DocumentViolation(
                codes.NoPrerequisiteOrder,
                "No prerequisite order exists — the dependency graph has a cycle, so the tasks " +
                "cannot be topologically sequenced."));
        }

        return violations;
    }

    /// <summary>
    /// Iterative DFS with an explicit frame stack over the clean adjacency. On the
    /// first back-edge (an edge into a node still on the active path) it walks the
    /// path to render the cycle members, closing the loop back to the re-entered
    /// node (e.g. <c>ST-2 -&gt; ST-4 -&gt; ST-2</c>). Returns <c>null</c> for a DAG.
    /// </summary>
    private static List<string>? FindCycle(
        IReadOnlyList<string> order,
        IReadOnlyDictionary<string, List<string>> adjacency)
    {
        const int Unvisited = 0, Active = 1, Finished = 2;
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in order)
            state[id] = Unvisited;

        foreach (var root in order)
        {
            if (state[root] != Unvisited)
                continue;

            var stack = new Stack<Frame>();
            var path = new List<string> { root };
            state[root] = Active;
            stack.Push(new Frame(root, adjacency[root]));

            while (stack.Count > 0)
            {
                var frame = stack.Peek();
                if (frame.Index < frame.Deps.Count)
                {
                    var dep = frame.Deps[frame.Index];
                    frame.Index++;

                    if (state[dep] == Unvisited)
                    {
                        state[dep] = Active;
                        path.Add(dep);
                        stack.Push(new Frame(dep, adjacency[dep]));
                    }
                    else if (state[dep] == Active)
                    {
                        // Back-edge into the active path: dep..current is the cycle.
                        var startIndex = path.IndexOf(dep);
                        var cycle = path.GetRange(startIndex, path.Count - startIndex);
                        cycle.Add(dep); // close the loop
                        return cycle;
                    }
                    // Finished node: a cross/forward edge, not a cycle — skip.
                }
                else
                {
                    state[frame.Node] = Finished;
                    stack.Pop();
                    path.RemoveAt(path.Count - 1);
                }
            }
        }

        return null;
    }

    private sealed class Frame
    {
        public Frame(string node, List<string> deps)
        {
            Node = node;
            Deps = deps;
        }

        public string Node { get; }
        public List<string> Deps { get; }
        public int Index { get; set; }
    }
}
