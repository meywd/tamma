import type { Route } from "./+types/_index";

export function meta({}: Route.MetaArgs) {
  return [
    { title: "Tamma Documentation Review" },
    { name: "description", content: "Collaborative documentation review platform" },
  ];
}

export default function Index() {
  return (
    <div style={{ fontFamily: "system-ui, sans-serif", lineHeight: "1.8", padding: "2rem" }}>
      <h1>Welcome to Tamma Documentation Review</h1>
      <p>
        A collaborative platform for reviewing, commenting, and suggesting edits to
        Tamma's technical documentation.
      </p>
      <h2>Features Coming Soon:</h2>
      <ul>
        <li>📝 Markdown documentation viewing with syntax highlighting</li>
        <li>💬 Inline comments on specific lines</li>
        <li>✏️ Edit suggestions with diff view</li>
        <li>💭 Document-level discussions</li>
        <li>🔐 Authentication with Cloudflare Access</li>
        <li>📊 Hierarchical navigation (Epics → Stories → Tasks)</li>
      </ul>
      <p>
        <strong>Status:</strong> Architecture complete, ready for implementation.
      </p>
      <p>
        Check out the documentation:
      </p>
      <ul>
        <li><code>ARCHITECTURE.md</code> - Complete technical design</li>
        <li><code>SETUP.md</code> - Setup instructions</li>
        <li><code>PROJECT_SUMMARY.md</code> - Implementation roadmap</li>
      </ul>
    </div>
  );
}
