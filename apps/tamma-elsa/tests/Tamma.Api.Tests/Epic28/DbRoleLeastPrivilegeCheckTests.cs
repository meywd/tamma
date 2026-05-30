using System;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-12 AC1+AC2 follow-up (2026-05-30 residual #3) — runtime
/// least-privilege assertion. The control-plane API MUST connect as
/// <c>tamma_app</c> (no CREATE DATABASE / CREATE ROLE). Nothing enforced
/// this at runtime: a misconfigured pod pointing the app connection at
/// the <c>tamma_provisioner</c> / <c>tamma_admin</c> URL would silently
/// run with escalated privileges, defeating the three-role split in
/// <c>scripts/db/postgres-roles.sql</c>.
///
/// <para>These cover the pure decision core
/// (<see cref="DbRoleLeastPrivilegeCheck.IsForbiddenAppUser"/> /
/// <see cref="DbRoleLeastPrivilegeCheck.Evaluate"/>). The live
/// <c>SELECT current_user</c> probe is an integration boundary exercised
/// against a real cluster — see the health-check class docs.</para>
///
/// <para>Critical gating: the assertion only HARD-FAILS in Production.
/// Development / Test deployments (the entire 2664-test suite) run on a
/// single default Postgres role with no split, so the check degrades to
/// a warning there and must stay green.</para>
/// </summary>
[TestFixture]
public class DbRoleLeastPrivilegeCheckTests
{
    // ── Pure decision: which role names are forbidden for the app ──────

    [TestCase("tamma_provisioner")]
    [TestCase("tamma_admin")]
    public void IsForbiddenAppUser_FlagsPrivilegedRoles(string user)
    {
        DbRoleLeastPrivilegeCheck.IsForbiddenAppUser(user).Should().BeTrue();
    }

    [TestCase("tamma_app")]
    [TestCase("tamma_t_0a1b2c3d")]
    [TestCase("postgres")]
    [TestCase("tamma")]
    public void IsForbiddenAppUser_AllowsAppAndOtherRoles(string user)
    {
        DbRoleLeastPrivilegeCheck.IsForbiddenAppUser(user).Should().BeFalse();
    }

    [TestCase("TAMMA_PROVISIONER")]
    [TestCase("Tamma_Admin")]
    public void IsForbiddenAppUser_IsCaseInsensitive(string user)
    {
        // Postgres folds unquoted identifiers to lower-case, but a probe
        // could surface mixed case; treat the match case-insensitively so
        // a casing quirk never lets a privileged role slip through.
        DbRoleLeastPrivilegeCheck.IsForbiddenAppUser(user).Should().BeTrue();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void IsForbiddenAppUser_TreatsMissingUserAsNotForbidden(string? user)
    {
        // A null/empty probe result is inconclusive, not a privilege
        // escalation — the Evaluate layer reports it as inconclusive
        // rather than a hard fail.
        DbRoleLeastPrivilegeCheck.IsForbiddenAppUser(user).Should().BeFalse();
    }

    // ── Evaluate: production hard-fail vs dev/test warning ─────────────

    [Test]
    public void Evaluate_Production_ForbiddenUser_IsFail()
    {
        DbRoleLeastPrivilegeCheck
            .Evaluate(isProduction: true, currentUser: "tamma_provisioner")
            .Should().Be(DbRoleLeastPrivilegeOutcome.Fail);
    }

    [Test]
    public void Evaluate_Production_AppUser_IsOk()
    {
        DbRoleLeastPrivilegeCheck
            .Evaluate(isProduction: true, currentUser: "tamma_app")
            .Should().Be(DbRoleLeastPrivilegeOutcome.Ok);
    }

    [Test]
    public void Evaluate_Development_ForbiddenUser_IsWarnOnly()
    {
        // The whole test suite runs as the default Postgres role under
        // Development; even a "forbidden" name must NOT hard-fail here.
        DbRoleLeastPrivilegeCheck
            .Evaluate(isProduction: false, currentUser: "tamma_admin")
            .Should().Be(DbRoleLeastPrivilegeOutcome.WarnOnly);
    }

    [Test]
    public void Evaluate_Development_DefaultRole_IsOk()
    {
        DbRoleLeastPrivilegeCheck
            .Evaluate(isProduction: false, currentUser: "postgres")
            .Should().Be(DbRoleLeastPrivilegeOutcome.Ok);
    }

    [Test]
    public void Evaluate_Production_AppUser_IsOk_Realistically()
    {
        DbRoleLeastPrivilegeCheck
            .Evaluate(isProduction: true, currentUser: "tamma_app")
            .Should().Be(DbRoleLeastPrivilegeOutcome.Ok);
    }
}
