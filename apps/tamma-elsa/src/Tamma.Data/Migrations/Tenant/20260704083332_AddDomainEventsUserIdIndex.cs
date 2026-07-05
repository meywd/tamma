using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.Tenant
{
    /// <summary>
    /// Story 4-7 (perf) — index the actor/userId time-travel lookups.
    /// <see cref="Repositories.EventRepository.QueryEventsAsync"/> (the event
    /// query API backing <c>GET /api/engine/events/query</c>) filters the tenant's
    /// <c>domain_events</c> (the 100%-audit stream) by
    /// <c>"Tags"-&gt;&gt;'userId' = $n</c> when an <c>actor</c> filter is supplied.
    /// Without a supporting index that predicate is a sequential scan of the entire
    /// stream (the perf AC targets &lt; 1s over 1M events). A btree EXPRESSION index
    /// on <c>((Tags-&gt;&gt;'userId'))</c> serves the equality directly (it matches
    /// the predicate expression exactly), and is far smaller than a whole-column GIN
    /// index on the jsonb <c>Tags</c>.
    ///
    /// <para>This is an "expression index the EF model cannot express" (same pattern
    /// as <see cref="AddDomainEventsCorrelationIdIndex"/> and
    /// <see cref="AddAgentTrailAgentIdIndex"/>), so it is raw SQL here — the model is
    /// deliberately unchanged, keeping the EF snapshot clean
    /// (<c>has-pending-model-changes</c> reports "No changes"). The unqualified table
    /// name resolves to the tenant schema via the per-tenant connection's
    /// <c>search_path</c>.</para>
    /// </summary>
    public partial class AddDomainEventsUserIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS ix_domain_events_tags_userid
                  ON domain_events (("Tags"->>'userId'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_domain_events_tags_userid;");
        }
    }
}
