using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Tamma.Api.Services.Billing;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-4 — shared in-memory harness for the subscription lifecycle unit
/// tests. Wires the real <see cref="SubscriptionMirrorUpdater"/> +
/// <see cref="BillingSubscriptionRepository"/> over an EF InMemory
/// <see cref="ControlPlaneDbContext"/>, with Stripe (subscription / schedule /
/// checkout) mocked at the <see cref="IStripeServices"/> boundary. Emitted DCB
/// events are captured in <see cref="Emitted"/>.
/// </summary>
internal sealed class SubscriptionHarness
{
    public ControlPlaneDbContext Db { get; }
    public Mock<IBillingProvider> Provider { get; }
    public Mock<IStripeServicesFactory> Factory { get; }
    public Mock<IStripeServices> Stripe { get; }
    public Mock<Stripe.SubscriptionService> Subscriptions { get; }
    public Mock<Stripe.SubscriptionScheduleService> Schedules { get; }
    public Mock<Stripe.Checkout.SessionService> Checkout { get; }
    public Mock<IBillingCatalog> Catalog { get; }
    public Mock<IEventRepository> Events { get; }
    public Mock<ITenantMembershipRepository> Members { get; }
    public BillingSubscriptionRepository Repo { get; }
    public SubscriptionMirrorUpdater Updater { get; }
    public SubscriptionService Service { get; }
    public List<DomainEvent> Emitted { get; } = new();

    private SubscriptionHarness(string dbName, bool enabled)
    {
        Db = new ControlPlaneDbContext(
            new DbContextOptionsBuilder<ControlPlaneDbContext>()
                .UseInMemoryDatabase(dbName)
                .Options);

        Provider = new Mock<IBillingProvider>();
        Provider.SetupGet(p => p.IsEnabled).Returns(enabled);

        Subscriptions = new Mock<Stripe.SubscriptionService>();
        Schedules = new Mock<Stripe.SubscriptionScheduleService>();
        Checkout = new Mock<Stripe.Checkout.SessionService>();

        Stripe = new Mock<IStripeServices>();
        Stripe.SetupGet(s => s.Subscriptions).Returns(Subscriptions.Object);
        Stripe.SetupGet(s => s.SubscriptionSchedules).Returns(Schedules.Object);
        Stripe.SetupGet(s => s.CheckoutSessions).Returns(Checkout.Object);

        Factory = new Mock<IStripeServicesFactory>();
        Factory.Setup(f => f.CreateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Stripe.Object);

        Events = new Mock<IEventRepository>();
        Events.Setup(e => e.AppendAsync(It.IsAny<DomainEvent>()))
            .Callback<DomainEvent>(Emitted.Add)
            .ReturnsAsync((DomainEvent d) => d);

        Catalog = new Mock<IBillingCatalog>();
        Members = new Mock<ITenantMembershipRepository>();
        Members.Setup(m => m.ListAllByTenantAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<TenantMembership>());

        Repo = new BillingSubscriptionRepository(Db);
        Updater = new SubscriptionMirrorUpdater(
            Db, Repo, Events.Object, TimeProvider.System,
            NullLogger<SubscriptionMirrorUpdater>.Instance);
        Service = new SubscriptionService(
            Provider.Object, Factory.Object, Catalog.Object, Repo, Updater, Db,
            Members.Object, Options.Create(new BillingOptions()),
            NullLogger<SubscriptionService>.Instance);
    }

    public static SubscriptionHarness Create(string dbName, bool enabled = true)
        => new(dbName, enabled);

    // ── seeding ──

    public void SeedPlan(string slug, decimal monthlyPriceUsd)
    {
        Db.Plans.Add(new Plan
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            DisplayName = slug,
            Status = "active",
            IsActive = true,
            MonthlyPriceUsd = monthlyPriceUsd,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        Db.SaveChanges();
    }

    /// <summary>Seed a catalog row (db + <see cref="IBillingCatalog"/> mock) for a slug.</summary>
    public BillingPlanPrice SeedCatalog(string slug, string basePriceId, string? seatsPriceId = null)
    {
        var row = new BillingPlanPrice
        {
            Id = Guid.NewGuid(),
            PlanSlug = slug,
            StripeProductId = $"prod_{slug}",
            StripePriceId = basePriceId,
            SeatsPriceId = seatsPriceId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        Db.BillingPlanPrices.Add(row);
        Db.SaveChanges();
        Catalog.Setup(c => c.GetBySlugAsync(slug, It.IsAny<CancellationToken>()))
            .ReturnsAsync(row);
        return row;
    }

    public Tenant SeedTenant(Guid tenantId, string plan = "free")
    {
        var tenant = new Tenant
        {
            Id = tenantId,
            Name = $"tenant-{tenantId:N}",
            Slug = $"t-{tenantId:N}"[..12],
            Plan = plan,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        Db.Tenants.Add(tenant);
        Db.SaveChanges();
        return tenant;
    }

    public void SeedCustomer(Guid tenantId, string stripeCustomerId = "cus_test")
    {
        Db.BillingCustomers.Add(new BillingCustomer
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StripeCustomerId = stripeCustomerId,
            BillingMode = "PlatformProvided",
            DefaultCurrency = "usd",
            TaxStatus = "none",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        Db.SaveChanges();
    }

    public BillingSubscription SeedMirror(
        Guid tenantId, string planSlug, string stripeSubId, string status = "active",
        int seats = 1, DateTime? periodEnd = null)
    {
        var mirror = new BillingSubscription
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StripeSubscriptionId = stripeSubId,
            PlanSlug = planSlug,
            Status = status,
            Seats = seats,
            CurrentPeriodStart = DateTime.UtcNow.AddDays(-5),
            CurrentPeriodEnd = periodEnd ?? DateTime.UtcNow.AddDays(25),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        Db.BillingSubscriptions.Add(mirror);
        Db.SaveChanges();
        return mirror;
    }

    public void SeedMembers(Guid tenantId, int count)
    {
        var list = Enumerable.Range(0, count)
            .Select(_ => new TenantMembership { TenantId = tenantId, UserId = Guid.NewGuid(), Role = "member" })
            .ToList();
        Members.Setup(m => m.ListAllByTenantAsync(tenantId)).ReturnsAsync(list);
    }

    // ── Stripe object fabrication ──

    public static Stripe.Subscription MakeSub(
        string id, string status, string basePriceId, DateTime periodStart, DateTime periodEnd,
        DateTime? trialEnd = null, bool cancelAtPeriodEnd = false, string baseItemId = "si_base",
        string? seatsPriceId = null, long seatsQty = 0, string seatItemId = "si_seat")
    {
        var items = new List<Stripe.SubscriptionItem>
        {
            new()
            {
                Id = baseItemId,
                Price = new Stripe.Price { Id = basePriceId },
                Quantity = 1,
                CurrentPeriodStart = periodStart,
                CurrentPeriodEnd = periodEnd,
            },
        };
        if (seatsPriceId is not null)
        {
            items.Add(new Stripe.SubscriptionItem
            {
                Id = seatItemId,
                Price = new Stripe.Price { Id = seatsPriceId },
                Quantity = seatsQty,
                CurrentPeriodStart = periodStart,
                CurrentPeriodEnd = periodEnd,
            });
        }

        return new Stripe.Subscription
        {
            Id = id,
            Status = status,
            CancelAtPeriodEnd = cancelAtPeriodEnd,
            TrialEnd = trialEnd,
            Items = new Stripe.StripeList<Stripe.SubscriptionItem> { Data = items },
        };
    }

    /// <summary>
    /// Fabricate a <see cref="Stripe.SubscriptionSchedule"/> as returned by a
    /// <c>from_subscription</c> create: a single phase[0] mirroring the current sub
    /// (base price item + current-period boundaries). The service reads this phase
    /// to build phase 1 of the two-phase downgrade update.
    /// </summary>
    public static Stripe.SubscriptionSchedule MakeSchedule(
        string id, string currentPriceId, DateTime periodStart, DateTime periodEnd,
        long currentQty = 1)
        => new()
        {
            Id = id,
            EndBehavior = "release",
            Phases = new List<Stripe.SubscriptionSchedulePhase>
            {
                new()
                {
                    StartDate = periodStart,
                    EndDate = periodEnd,
                    Items = new List<Stripe.SubscriptionSchedulePhaseItem>
                    {
                        new() { Price = new Stripe.Price { Id = currentPriceId }, Quantity = currentQty },
                    },
                },
            },
        };
}
