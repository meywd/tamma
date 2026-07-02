using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Billing;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-4 (AC11) — single-user mode: the <see cref="SubscriptionService"/>
/// resolves the disabled billing provider (<c>IsEnabled = false</c>), so every
/// mutating call is a hard SaaS-only error and ZERO Stripe calls are made. GET
/// still returns the free-tier default.
/// </summary>
[TestFixture]
public class NullBillingSubscriptionTests
{
    [Test]
    public async Task SingleUser_Mutations_Throw_SaasOnly_With_Zero_Stripe_Calls()
    {
        var h = SubscriptionHarness.Create(
            nameof(SingleUser_Mutations_Throw_SaasOnly_With_Zero_Stripe_Calls), enabled: false);
        var tenantId = Guid.NewGuid();

        Func<Task>[] mutations =
        {
            () => h.Service.CreateCheckoutSessionAsync(tenantId, "team", null, null),
            () => h.Service.ChangePlanAsync(tenantId, "team"),
            () => h.Service.CancelAsync(tenantId, false),
            () => h.Service.ChangeSeatsAsync(tenantId, 3),
        };

        foreach (var mutate in mutations)
        {
            (await mutate.Should().ThrowAsync<Tamma.Core.TammaError>())
                .Where(e => e.Code == SubscriptionService.SaasOnlyCode);
        }

        // Zero Stripe surface touched (AC11).
        h.Factory.Verify(f => f.CreateAsync(It.IsAny<CancellationToken>()), Times.Never);
        h.Checkout.VerifyNoOtherCalls();
        h.Subscriptions.VerifyNoOtherCalls();
        h.Schedules.VerifyNoOtherCalls();
    }

    [Test]
    public async Task SingleUser_Get_Returns_Free_Default()
    {
        var h = SubscriptionHarness.Create(
            nameof(SingleUser_Get_Returns_Free_Default), enabled: false);

        var projection = await h.Service.GetAsync(Guid.NewGuid());

        projection.PlanSlug.Should().Be("free");
        projection.Status.Should().Be("active");
        projection.Seats.Should().Be(1);
    }
}
