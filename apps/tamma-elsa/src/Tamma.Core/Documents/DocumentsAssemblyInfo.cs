using System.Runtime.CompilerServices;

// Exposes the Documents namespace's internal pure cores (notably
// DocumentTypeRegistry.BuildIndex — Story 39-2 AC5d) to the drift-test project.
// Declared here, in a source file under the story's allowed diff surface, rather
// than in the csproj.
[assembly: InternalsVisibleTo("Tamma.Core.Tests")]
