# Task 5: Resource Management

**Story**: 1.3 - Organization Management & Multi-Tenancy  
**Task**: 5 - Resource Management  
**Priority**: High  
**Estimated Time**: 10 hours

## Description

Implement comprehensive resource management system for organizations including quotas, usage tracking, resource allocation, and billing integration. This task ensures fair resource distribution, prevents abuse, and provides visibility into resource consumption across the platform.

## Acceptance Criteria

### 1. Resource Quotas and Limits

- [ ] Define resource types (API calls, storage, compute, team members)
- [ ] Implement per-organization quota management
- [ ] Support tier-based quota limits (free, pro, enterprise)
- [ ] Real-time quota enforcement and validation

### 2. Usage Tracking and Monitoring

- [ ] Track resource consumption in real-time
- [ ] Implement usage aggregation and reporting
- [ ] Support historical usage data and trends
- [ ] Provide usage alerts and notifications

### 3. Resource Allocation Management

- [ ] Dynamic resource allocation based on tier
- [ ] Support resource pooling and sharing
- [ ] Implement resource reservation system
- [ ] Handle resource conflicts and resolution

### 4. Billing Integration

- [ ] Calculate usage-based billing
- [ ] Support subscription management
- [ ] Generate invoices and receipts
- [ ] Handle payment processing and failures

### 5. Administration and Analytics

- [ ] Admin dashboard for resource management
- [ ] Usage analytics and insights
- [ ] Resource optimization recommendations
- [ ] Compliance and audit reporting

## Implementation Plan

### Step 1: Database Schema for Resource Management

#### 1.1 Resource Types and Tiers

```sql
-- Resource types table
CREATE TABLE resource_types (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name VARCHAR(50) UNIQUE NOT NULL,
    description TEXT,
    unit VARCHAR(20) NOT NULL, -- 'requests', 'bytes', 'seconds', 'seats'
    is_metered BOOLEAN DEFAULT true,
    default_quota BIGINT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Subscription tiers table
CREATE TABLE subscription_tiers (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name VARCHAR(50) UNIQUE NOT NULL,
    description TEXT,
    price_monthly DECIMAL(10,2),
    price_yearly DECIMAL(10,2),
    features JSONB,
    is_active BOOLEAN DEFAULT true,
    sort_order INTEGER DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Tier resource quotas table
CREATE TABLE tier_resource_quotas (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tier_id UUID NOT NULL REFERENCES subscription_tiers(id) ON DELETE CASCADE,
    resource_type_id UUID NOT NULL REFERENCES resource_types(id) ON DELETE CASCADE,
    quota_limit BIGINT NOT NULL,
    is_unlimited BOOLEAN DEFAULT false,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    UNIQUE(tier_id, resource_type_id)
);

-- Insert initial data
INSERT INTO resource_types (name, description, unit, is_metered, default_quota) VALUES
('api_requests', 'API requests per month', 'requests', true, 10000),
('storage', 'Storage space in GB', 'bytes', true, 10737418240), -- 10GB
('compute_time', 'Compute time in seconds', 'seconds', true, 3600), -- 1 hour
('team_members', 'Number of team members', 'seats', false, 5),
('projects', 'Number of projects', 'seats', false, 10);

INSERT INTO subscription_tiers (name, description, price_monthly, price_yearly, features, sort_order) VALUES
('Free', 'Basic tier for individuals and small teams', 0.00, 0.00, '{"api_requests": 10000, "storage": 10737418240, "team_members": 5}', 1),
('Pro', 'Professional tier for growing teams', 29.00, 290.00, '{"api_requests": 100000, "storage": 107374182400, "team_members": 20}', 2),
('Enterprise', 'Enterprise tier with unlimited resources', 99.00, 990.00, '{"api_requests": 1000000, "storage": 1073741824000, "team_members": 100}', 3);

-- Set up tier quotas
INSERT INTO tier_resource_quotas (tier_id, resource_type_id, quota_limit, is_unlimited)
SELECT
    t.id as tier_id,
    rt.id as resource_type_id,
    CASE
        WHEN t.name = 'Free' THEN rt.default_quota
        WHEN t.name = 'Pro' THEN rt.default_quota * 10
        WHEN t.name = 'Enterprise' THEN NULL -- Unlimited
    END as quota_limit,
    CASE WHEN t.name = 'Enterprise' THEN true ELSE false END as is_unlimited
FROM subscription_tiers t
CROSS JOIN resource_types rt;
```

#### 1.2 Organization Resources and Usage

```sql
-- Organization subscriptions table
CREATE TABLE organization_subscriptions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    tier_id UUID NOT NULL REFERENCES subscription_tiers(id),
    status VARCHAR(20) NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'cancelled', 'suspended', 'trial')),
    trial_ends_at TIMESTAMPTZ,
    current_period_starts_at TIMESTAMPTZ NOT NULL,
    current_period_ends_at TIMESTAMPTZ NOT NULL,
    cancelled_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    UNIQUE(organization_id)
);

-- Organization resource quotas (can override tier defaults)
CREATE TABLE organization_resource_quotas (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    resource_type_id UUID NOT NULL REFERENCES resource_types(id) ON DELETE CASCADE,
    quota_limit BIGINT,
    is_unlimited BOOLEAN DEFAULT false,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    UNIQUE(organization_id, resource_type_id)
);

-- Resource usage tracking table
CREATE TABLE resource_usage (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    resource_type_id UUID NOT NULL REFERENCES resource_types(id) ON DELETE CASCADE,
    usage_amount BIGINT NOT NULL,
    usage_date DATE NOT NULL,
    hour INTEGER NOT NULL CHECK (hour >= 0 AND hour <= 23),
    metadata JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    UNIQUE(organization_id, resource_type_id, usage_date, hour)
);

-- Resource usage events (real-time tracking)
CREATE TABLE resource_usage_events (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    resource_type_id UUID NOT NULL REFERENCES resource_types(id) ON DELETE CASCADE,
    usage_amount BIGINT NOT NULL,
    event_type VARCHAR(20) NOT NULL DEFAULT 'consumption' CHECK (event_type IN ('consumption', 'allocation', 'deallocation')),
    resource_id UUID,
    user_id UUID REFERENCES users(id),
    metadata JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indexes for performance
CREATE INDEX idx_resource_usage_org_date ON resource_usage(organization_id, usage_date);
CREATE INDEX idx_resource_usage_events_org ON resource_usage_events(organization_id, created_at);
CREATE INDEX idx_org_subscriptions_org_id ON organization_subscriptions(organization_id);
CREATE INDEX idx_org_subscriptions_status ON organization_subscriptions(status);
```

#### 1.3 Billing and Invoicing

```sql
-- Billing information table
CREATE TABLE billing_information (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    billing_email VARCHAR(255) NOT NULL,
    company_name VARCHAR(255),
    tax_id VARCHAR(50),
    address JSONB,
    payment_method JSONB,
    is_default BOOLEAN DEFAULT false,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Invoices table
CREATE TABLE invoices (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    invoice_number VARCHAR(50) UNIQUE NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'sent', 'paid', 'overdue', 'cancelled')),
    currency VARCHAR(3) NOT NULL DEFAULT 'USD',
    subtotal DECIMAL(10,2) NOT NULL,
    tax_amount DECIMAL(10,2) NOT NULL DEFAULT 0,
    total_amount DECIMAL(10,2) NOT NULL,
    due_date TIMESTAMPTZ,
    paid_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Invoice line items
CREATE TABLE invoice_line_items (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    invoice_id UUID NOT NULL REFERENCES invoices(id) ON DELETE CASCADE,
    description TEXT NOT NULL,
    quantity DECIMAL(10,2) NOT NULL,
    unit_price DECIMAL(10,2) NOT NULL,
    total DECIMAL(10,2) NOT NULL,
    metadata JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Usage alerts table
CREATE TABLE usage_alerts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    resource_type_id UUID NOT NULL REFERENCES resource_types(id) ON DELETE CASCADE,
    alert_type VARCHAR(20) NOT NULL CHECK (alert_type IN ('quota_warning', 'quota_exceeded', 'usage_spike')),
    threshold_percentage INTEGER,
    current_usage BIGINT,
    quota_limit BIGINT,
    is_sent BOOLEAN DEFAULT false,
    sent_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

### Step 2: RLS Policies for Resource Management

#### 2.1 Organization Resources RLS

```sql
-- Enable RLS on resource tables
ALTER TABLE organization_subscriptions ENABLE ROW LEVEL SECURITY;
ALTER TABLE organization_resource_quotas ENABLE ROW LEVEL SECURITY;
ALTER TABLE resource_usage ENABLE ROW LEVEL SECURITY;
ALTER TABLE resource_usage_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE billing_information ENABLE ROW LEVEL SECURITY;
ALTER TABLE invoices ENABLE ROW LEVEL SECURITY;

-- Policy for organization subscriptions
CREATE POLICY org_subscriptions_view_own ON organization_subscriptions
    FOR ALL
    TO authenticated
    USING (organization_id IN (
        SELECT id FROM organizations WHERE owner_id = auth.uid()
        UNION
        SELECT organization_id FROM organization_members
        WHERE user_id = auth.uid() AND status = 'active'
    ));

-- Policy for resource usage (view own organization's usage)
CREATE POLICY resource_usage_view_own ON resource_usage
    FOR SELECT
    TO authenticated
    USING (organization_id IN (
        SELECT id FROM organizations WHERE owner_id = auth.uid()
        UNION
        SELECT organization_id FROM organization_members
        WHERE user_id = auth.uid() AND status = 'active'
    ));

-- Policy for billing information
CREATE POLICY billing_info_view_own ON billing_information
    FOR ALL
    TO authenticated
    USING (organization_id IN (
        SELECT id FROM organizations WHERE owner_id = auth.uid()
    ));

-- Policy for invoices
CREATE POLICY invoices_view_own ON invoices
    FOR SELECT
    TO authenticated
    USING (organization_id IN (
        SELECT id FROM organizations WHERE owner_id = auth.uid()
        UNION
        SELECT organization_id FROM organization_members
        WHERE user_id = auth.uid() AND status = 'active'
        AND EXISTS (
            SELECT 1 FROM member_roles mr
            JOIN roles r ON mr.role_id = r.id
            WHERE mr.organization_member_id = organization_members.id
            AND r.permissions @> '["billing:view"]'
        )
    ));
```

### Step 3: Resource Management Services

#### 3.1 Resource Quota Service

```typescript
// packages/orchestrator/src/services/resource-quota.service.ts
import { db } from '@tamma/database';
import { eq, and, desc, lt, gte } from 'drizzle-orm';
import dayjs from 'dayjs';

export interface ResourceQuota {
  resourceTypeId: string;
  resourceType: string;
  unit: string;
  quotaLimit: number | null;
  isUnlimited: boolean;
  currentUsage: number;
  usagePercentage: number;
  remaining: number | null;
}

export interface UsageCheckRequest {
  organizationId: string;
  resourceType: string;
  amount: number;
  userId?: string;
  metadata?: Record<string, unknown>;
}

export interface UsageCheckResponse {
  allowed: boolean;
  quota: ResourceQuota;
  reason?: string;
}

export class ResourceQuotaService {
  async checkQuota(request: UsageCheckRequest): Promise<UsageCheckResponse> {
    const { organizationId, resourceType, amount } = request;

    // Get resource type
    const [resourceTypeData] = await db
      .select()
      .from(resourceTypes)
      .where(eq(resourceTypes.name, resourceType))
      .limit(1);

    if (!resourceTypeData) {
      throw new Error(`Unknown resource type: ${resourceType}`);
    }

    // Get organization quota
    const quota = await this.getOrganizationQuota(organizationId, resourceTypeData.id);

    // Check if unlimited
    if (quota.isUnlimited) {
      return {
        allowed: true,
        quota,
      };
    }

    // Check if quota would be exceeded
    if (quota.remaining !== null && quota.remaining < amount) {
      return {
        allowed: false,
        quota,
        reason: `Quota would be exceeded. Requested: ${amount}, Available: ${quota.remaining}`,
      };
    }

    return {
      allowed: true,
      quota,
    };
  }

  async recordUsage(request: UsageCheckRequest & { allowed: boolean }): Promise<void> {
    const { organizationId, resourceType, amount, userId, metadata, allowed } = request;

    if (!allowed) {
      return;
    }

    // Get resource type
    const [resourceTypeData] = await db
      .select()
      .from(resourceTypes)
      .where(eq(resourceTypes.name, resourceType))
      .limit(1);

    if (!resourceTypeData) {
      throw new Error(`Unknown resource type: ${resourceType}`);
    }

    const now = dayjs();
    const usageDate = now.format('YYYY-MM-DD');
    const hour = now.hour();

    await db.transaction(async (tx) => {
      // Record usage event
      await tx.insert(resourceUsageEvents).values({
        organizationId,
        resourceTypeId: resourceTypeData.id,
        usageAmount: amount,
        eventType: 'consumption',
        userId,
        metadata,
      });

      // Update hourly usage aggregation
      await tx
        .insert(resourceUsage)
        .values({
          organizationId,
          resourceTypeId: resourceTypeData.id,
          usageAmount: amount,
          usageDate,
          hour,
          metadata,
        })
        .onConflictDoUpdate({
          target: [
            resourceUsage.organizationId,
            resourceUsage.resourceTypeId,
            resourceUsage.usageDate,
            resourceUsage.hour,
          ],
          set: {
            usageAmount: sql`${resourceUsage.usageAmount} + ${amount}`,
          },
        });
    });

    // Check for quota alerts
    await this.checkUsageAlerts(organizationId, resourceTypeData.id);
  }

  async getOrganizationQuota(
    organizationId: string,
    resourceTypeId: string
  ): Promise<ResourceQuota> {
    // Get organization-specific quota or fall back to tier quota
    const [orgQuota] = await db
      .select()
      .from(organizationResourceQuotas)
      .where(
        and(
          eq(organizationResourceQuotas.organizationId, organizationId),
          eq(organizationResourceQuotas.resourceTypeId, resourceTypeId)
        )
      )
      .limit(1);

    let quotaLimit: number | null = null;
    let isUnlimited = false;

    if (orgQuota) {
      quotaLimit = orgQuota.quotaLimit;
      isUnlimited = orgQuota.isUnlimited;
    } else {
      // Fall back to tier quota
      const [tierQuota] = await db
        .select({
          quotaLimit: tierResourceQuotas.quotaLimit,
          isUnlimited: tierResourceQuotas.isUnlimited,
          resourceType: resourceTypes.name,
          unit: resourceTypes.unit,
        })
        .from(tierResourceQuotas)
        .innerJoin(subscriptionTiers, eq(tierResourceQuotas.tierId, subscriptionTiers.id))
        .innerJoin(resourceTypes, eq(tierResourceQuotas.resourceTypeId, resourceTypes.id))
        .innerJoin(
          organizationSubscriptions,
          eq(subscriptionTiers.id, organizationSubscriptions.tierId)
        )
        .where(
          and(
            eq(organizationSubscriptions.organizationId, organizationId),
            eq(organizationSubscriptions.status, 'active'),
            eq(tierResourceQuotas.resourceTypeId, resourceTypeId)
          )
        )
        .limit(1);

      if (tierQuota) {
        quotaLimit = tierQuota.quotaLimit;
        isUnlimited = tierQuota.isUnlimited;
      }
    }

    // Get current usage for the current billing period
    const currentUsage = await this.getCurrentUsage(organizationId, resourceTypeId);

    const usagePercentage = quotaLimit && !isUnlimited ? (currentUsage / quotaLimit) * 100 : 0;
    const remaining = quotaLimit && !isUnlimited ? Math.max(0, quotaLimit - currentUsage) : null;

    return {
      resourceTypeId,
      resourceType: '', // Will be filled by caller
      unit: '', // Will be filled by caller
      quotaLimit,
      isUnlimited,
      currentUsage,
      usagePercentage,
      remaining,
    };
  }

  async getCurrentUsage(organizationId: string, resourceTypeId: string): Promise<number> {
    // Get current billing period start
    const [subscription] = await db
      .select()
      .from(organizationSubscriptions)
      .where(eq(organizationSubscriptions.organizationId, organizationId))
      .limit(1);

    if (!subscription) {
      return 0;
    }

    const periodStart = dayjs(subscription.currentPeriodStartsAt);
    const today = dayjs();

    // Sum usage from period start to today
    const [usage] = await db
      .select({
        total: sql<number>`SUM(${resourceUsage.usageAmount})`,
      })
      .from(resourceUsage)
      .where(
        and(
          eq(resourceUsage.organizationId, organizationId),
          eq(resourceUsage.resourceTypeId, resourceTypeId),
          gte(resourceUsage.usageDate, periodStart.format('YYYY-MM-DD')),
          lt(resourceUsage.usageDate, today.format('YYYY-MM-DD'))
        )
      );

    return usage?.total || 0;
  }

  async getAllQuotas(organizationId: string): Promise<ResourceQuota[]> {
    const resourceTypesList = await db.select().from(resourceTypes);

    const quotas = await Promise.all(
      resourceTypesList.map(async (resourceType) => {
        const quota = await this.getOrganizationQuota(organizationId, resourceType.id);
        return {
          ...quota,
          resourceType: resourceType.name,
          unit: resourceType.unit,
        };
      })
    );

    return quotas;
  }

  async updateOrganizationQuota(
    organizationId: string,
    resourceTypeId: string,
    quotaLimit: number | null,
    isUnlimited: boolean = false
  ): Promise<void> {
    await db
      .insert(organizationResourceQuotas)
      .values({
        organizationId,
        resourceTypeId,
        quotaLimit,
        isUnlimited,
      })
      .onConflictDoUpdate({
        target: [
          organizationResourceQuotas.organizationId,
          organizationResourceQuotas.resourceTypeId,
        ],
        set: {
          quotaLimit,
          isUnlimited,
          updatedAt: new Date(),
        },
      });
  }

  private async checkUsageAlerts(organizationId: string, resourceTypeId: string): Promise<void> {
    const quota = await this.getOrganizationQuota(organizationId, resourceTypeId);

    if (quota.isUnlimited || !quota.quotaLimit) {
      return;
    }

    // Check for quota warning (80%)
    if (quota.usagePercentage >= 80 && quota.usagePercentage < 85) {
      await this.createUsageAlert({
        organizationId,
        resourceTypeId,
        alertType: 'quota_warning',
        thresholdPercentage: 80,
        currentUsage: quota.currentUsage,
        quotaLimit: quota.quotaLimit,
      });
    }

    // Check for quota exceeded
    if (quota.usagePercentage >= 100) {
      await this.createUsageAlert({
        organizationId,
        resourceTypeId,
        alertType: 'quota_exceeded',
        thresholdPercentage: 100,
        currentUsage: quota.currentUsage,
        quotaLimit: quota.quotaLimit,
      });
    }
  }

  private async createUsageAlert(alertData: {
    organizationId: string;
    resourceTypeId: string;
    alertType: 'quota_warning' | 'quota_exceeded' | 'usage_spike';
    thresholdPercentage?: number;
    currentUsage: number;
    quotaLimit: number;
  }): Promise<void> {
    // Check if alert already sent in last 24 hours
    const [existingAlert] = await db
      .select()
      .from(usageAlerts)
      .where(
        and(
          eq(usageAlerts.organizationId, alertData.organizationId),
          eq(usageAlerts.resourceTypeId, alertData.resourceTypeId),
          eq(usageAlerts.alertType, alertData.alertType),
          gte(usageAlerts.createdAt, dayjs().subtract(24, 'hours').toDate())
        )
      )
      .limit(1);

    if (existingAlert) {
      return;
    }

    await db.insert(usageAlerts).values(alertData);

    // TODO: Send notification (email, webhook, etc.)
  }
}
```

#### 3.2 Usage Analytics Service

```typescript
// packages/orchestrator/src/services/usage-analytics.service.ts
import { db } from '@tamma/database';
import { eq, and, gte, lte, desc, sql } from 'drizzle-orm';
import dayjs from 'dayjs';

export interface UsageAnalytics {
  resourceType: string;
  currentUsage: number;
  previousUsage: number;
  growthRate: number;
  projectedUsage: number;
  costImpact: number;
}

export interface UsageReport {
  organizationId: string;
  period: {
    start: string;
    end: string;
  };
  totalUsage: Record<string, number>;
  costBreakdown: Record<string, number>;
  trends: UsageAnalytics[];
  recommendations: string[];
}

export class UsageAnalyticsService {
  async generateUsageReport(
    organizationId: string,
    startDate: Date,
    endDate: Date
  ): Promise<UsageReport> {
    const resourceTypesList = await db.select().from(resourceTypes);
    const subscription = await this.getOrganizationSubscription(organizationId);

    // Get usage data for the period
    const usageData = await this.getUsageData(organizationId, startDate, endDate);

    // Get previous period data for comparison
    const previousStartDate = dayjs(startDate).subtract(
      dayjs(endDate).diff(startDate, 'days'),
      'days'
    );
    const previousEndDate = startDate;
    const previousUsageData = await this.getUsageData(
      organizationId,
      previousStartDate.toDate(),
      previousEndDate
    );

    // Calculate analytics for each resource type
    const trends: UsageAnalytics[] = [];
    const totalUsage: Record<string, number> = {};
    const costBreakdown: Record<string, number> = {};

    for (const resourceType of resourceTypesList) {
      const currentUsage = usageData[resourceType.id] || 0;
      const previousUsage = previousUsageData[resourceType.id] || 0;
      const growthRate =
        previousUsage > 0 ? ((currentUsage - previousUsage) / previousUsage) * 100 : 0;

      // Project usage for next period
      const projectedUsage = currentUsage * (1 + growthRate / 100);

      // Calculate cost impact (simplified - would need pricing rules)
      const costImpact = this.calculateCostImpact(resourceType, currentUsage, subscription);

      trends.push({
        resourceType: resourceType.name,
        currentUsage,
        previousUsage,
        growthRate,
        projectedUsage,
        costImpact,
      });

      totalUsage[resourceType.name] = currentUsage;
      costBreakdown[resourceType.name] = costImpact;
    }

    // Generate recommendations
    const recommendations = this.generateRecommendations(trends, subscription);

    return {
      organizationId,
      period: {
        start: startDate.toISOString(),
        end: endDate.toISOString(),
      },
      totalUsage,
      costBreakdown,
      trends,
      recommendations,
    };
  }

  async getUsageData(
    organizationId: string,
    startDate: Date,
    endDate: Date
  ): Promise<Record<string, number>> {
    const usage = await db
      .select({
        resourceTypeId: resourceUsage.resourceTypeId,
        totalUsage: sql<number>`SUM(${resourceUsage.usageAmount})`,
      })
      .from(resourceUsage)
      .where(
        and(
          eq(resourceUsage.organizationId, organizationId),
          gte(resourceUsage.usageDate, dayjs(startDate).format('YYYY-MM-DD')),
          lte(resourceUsage.usageDate, dayjs(endDate).format('YYYY-MM-DD'))
        )
      )
      .groupBy(resourceUsage.resourceTypeId);

    return usage.reduce(
      (acc, row) => {
        acc[row.resourceTypeId] = row.totalUsage;
        return acc;
      },
      {} as Record<string, number>
    );
  }

  async getTopResourceConsumers(
    organizationId: string,
    resourceType: string,
    limit: number = 10
  ): Promise<Array<{ userId: string; usage: number; percentage: number }>> {
    const [resourceTypeData] = await db
      .select()
      .from(resourceTypes)
      .where(eq(resourceTypes.name, resourceType))
      .limit(1);

    if (!resourceTypeData) {
      throw new Error(`Unknown resource type: ${resourceType}`);
    }

    const consumers = await db
      .select({
        userId: resourceUsageEvents.userId,
        totalUsage: sql<number>`SUM(${resourceUsageEvents.usageAmount})`,
      })
      .from(resourceUsageEvents)
      .where(
        and(
          eq(resourceUsageEvents.organizationId, organizationId),
          eq(resourceUsageEvents.resourceTypeId, resourceTypeData.id),
          eq(resourceUsageEvents.eventType, 'consumption'),
          gte(resourceUsageEvents.createdAt, dayjs().subtract(30, 'days').toDate())
        )
      )
      .groupBy(resourceUsageEvents.userId)
      .orderBy(desc(sql`SUM(${resourceUsageEvents.usageAmount})`))
      .limit(limit);

    const totalUsage = consumers.reduce((sum, consumer) => sum + consumer.totalUsage, 0);

    return consumers.map((consumer) => ({
      userId: consumer.userId || 'system',
      usage: consumer.totalUsage,
      percentage: totalUsage > 0 ? (consumer.totalUsage / totalUsage) * 100 : 0,
    }));
  }

  async getUsageTrends(
    organizationId: string,
    resourceType: string,
    days: number = 30
  ): Promise<Array<{ date: string; usage: number }>> {
    const [resourceTypeData] = await db
      .select()
      .from(resourceTypes)
      .where(eq(resourceTypes.name, resourceType))
      .limit(1);

    if (!resourceTypeData) {
      throw new Error(`Unknown resource type: ${resourceType}`);
    }

    const startDate = dayjs().subtract(days, 'days').format('YYYY-MM-DD');

    const trends = await db
      .select({
        date: resourceUsage.usageDate,
        usage: sql<number>`SUM(${resourceUsage.usageAmount})`,
      })
      .from(resourceUsage)
      .where(
        and(
          eq(resourceUsage.organizationId, organizationId),
          eq(resourceUsage.resourceTypeId, resourceTypeData.id),
          gte(resourceUsage.usageDate, startDate)
        )
      )
      .groupBy(resourceUsage.usageDate)
      .orderBy(resourceUsage.usageDate);

    return trends.map((trend) => ({
      date: trend.date,
      usage: trend.usage,
    }));
  }

  private async getOrganizationSubscription(organizationId: string) {
    const [subscription] = await db
      .select({
        tier: subscriptionTiers,
      })
      .from(organizationSubscriptions)
      .innerJoin(subscriptionTiers, eq(organizationSubscriptions.tierId, subscriptionTiers.id))
      .where(eq(organizationSubscriptions.organizationId, organizationId))
      .limit(1);

    return subscription?.tier;
  }

  private calculateCostImpact(resourceType: any, usage: number, subscription: any): number {
    // Simplified cost calculation
    // In reality, this would use complex pricing rules, tier pricing, etc.
    const baseCosts: Record<string, number> = {
      api_requests: 0.0001, // $0.0001 per request
      storage: 0.000000023, // $0.023 per GB
      compute_time: 0.01, // $0.01 per second
    };

    return (baseCosts[resourceType.name] || 0) * usage;
  }

  private generateRecommendations(trends: UsageAnalytics[], subscription: any): string[] {
    const recommendations: string[] = [];

    for (const trend of trends) {
      if (trend.growthRate > 50) {
        recommendations.push(
          `High growth detected in ${trend.resourceType} usage (${trend.growthRate.toFixed(1)}%). Consider upgrading your plan.`
        );
      }

      if (trend.costImpact > 100) {
        recommendations.push(
          `${trend.resourceType} is contributing significantly to costs ($${trend.costImpact.toFixed(2)}). Review usage patterns.`
        );
      }

      if (trend.projectedUsage > 0) {
        // Check if projected usage exceeds quota
        // This would require quota information
      }
    }

    // General recommendations based on tier
    if (subscription?.name === 'Free') {
      recommendations.push(
        'Consider upgrading to Pro tier for higher limits and additional features.'
      );
    }

    return recommendations;
  }
}
```

### Step 4: API Endpoints for Resource Management

#### 4.1 Resource Quota Endpoints

```typescript
// packages/api/src/routes/resources.ts
import { FastifyInstance } from 'fastify';
import { resourceQuotaService } from '@tamma/orchestrator/services';
import { authenticate, authorize } from '../middleware/auth';

export default async function resourceRoutes(fastify: FastifyInstance) {
  // Get organization quotas
  fastify.get(
    '/organizations/:orgId/quotas',
    {
      preHandler: [authenticate, authorize('resource:view')],
      schema: {
        params: {
          type: 'object',
          properties: {
            orgId: { type: 'string', format: 'uuid' },
          },
          required: ['orgId'],
        },
      },
    },
    async (request, reply) => {
      const { orgId } = request.params as { orgId: string };

      try {
        const quotas = await resourceQuotaService.getAllQuotas(orgId);
        return reply.send(quotas);
      } catch (error) {
        fastify.log.error(error);
        return reply.status(400).send({ error: error.message });
      }
    }
  );

  // Check quota before resource consumption
  fastify.post(
    '/organizations/:orgId/quotas/check',
    {
      preHandler: [authenticate],
      schema: {
        params: {
          type: 'object',
          properties: {
            orgId: { type: 'string', format: 'uuid' },
          },
          required: ['orgId'],
        },
        body: {
          type: 'object',
          properties: {
            resourceType: { type: 'string' },
            amount: { type: 'number' },
            metadata: { type: 'object' },
          },
          required: ['resourceType', 'amount'],
        },
      },
    },
    async (request, reply) => {
      const { orgId } = request.params as { orgId: string };
      const { resourceType, amount, metadata } = request.body as any;
      const userId = request.user.id;

      try {
        const check = await resourceQuotaService.checkQuota({
          organizationId: orgId,
          resourceType,
          amount,
          userId,
          metadata,
        });

        // Record usage if allowed
        if (check.allowed) {
          await resourceQuotaService.recordUsage({
            organizationId: orgId,
            resourceType,
            amount,
            userId,
            metadata,
            allowed: true,
          });
        }

        return reply.send(check);
      } catch (error) {
        fastify.log.error(error);
        return reply.status(400).send({ error: error.message });
      }
    }
  );

  // Update organization quota (admin only)
  fastify.patch(
    '/organizations/:orgId/quotas/:resourceTypeId',
    {
      preHandler: [authenticate, authorize('resource:manage')],
      schema: {
        params: {
          type: 'object',
          properties: {
            orgId: { type: 'string', format: 'uuid' },
            resourceTypeId: { type: 'string', format: 'uuid' },
          },
          required: ['orgId', 'resourceTypeId'],
        },
        body: {
          type: 'object',
          properties: {
            quotaLimit: { type: 'number' },
            isUnlimited: { type: 'boolean' },
          },
        },
      },
    },
    async (request, reply) => {
      const { orgId, resourceTypeId } = request.params as { orgId: string; resourceTypeId: string };
      const { quotaLimit, isUnlimited } = request.body as any;

      try {
        await resourceQuotaService.updateOrganizationQuota(
          orgId,
          resourceTypeId,
          quotaLimit,
          isUnlimited
        );

        return reply.send({ message: 'Quota updated successfully' });
      } catch (error) {
        fastify.log.error(error);
        return reply.status(400).send({ error: error.message });
      }
    }
  );
}
```

#### 4.2 Usage Analytics Endpoints

```typescript
// packages/api/src/routes/analytics.ts
import { FastifyInstance } from 'fastify';
import { usageAnalyticsService } from '@tamma/orchestrator/services';
import { authenticate, authorize } from '../middleware/auth';

export default async function analyticsRoutes(fastify: FastifyInstance) {
  // Generate usage report
  fastify.get(
    '/organizations/:orgId/analytics/usage-report',
    {
      preHandler: [authenticate, authorize('analytics:view')],
      schema: {
        params: {
          type: 'object',
          properties: {
            orgId: { type: 'string', format: 'uuid' },
          },
          required: ['orgId'],
        },
        querystring: {
          type: 'object',
          properties: {
            startDate: { type: 'string', format: 'date' },
            endDate: { type: 'string', format: 'date' },
          },
        },
      },
    },
    async (request, reply) => {
      const { orgId } = request.params as { orgId: string };
      const { startDate, endDate } = request.query as any;

      const start = startDate
        ? new Date(startDate)
        : new Date(Date.now() - 30 * 24 * 60 * 60 * 1000);
      const end = endDate ? new Date(endDate) : new Date();

      try {
        const report = await usageAnalyticsService.generateUsageReport(orgId, start, end);
        return reply.send(report);
      } catch (error) {
        fastify.log.error(error);
        return reply.status(400).send({ error: error.message });
      }
    }
  );

  // Get top resource consumers
  fastify.get(
    '/organizations/:orgId/analytics/top-consumers',
    {
      preHandler: [authenticate, authorize('analytics:view')],
      schema: {
        params: {
          type: 'object',
          properties: {
            orgId: { type: 'string', format: 'uuid' },
          },
          required: ['orgId'],
        },
        querystring: {
          type: 'object',
          properties: {
            resourceType: { type: 'string' },
            limit: { type: 'number', default: 10 },
          },
          required: ['resourceType'],
        },
      },
    },
    async (request, reply) => {
      const { orgId } = request.params as { orgId: string };
      const { resourceType, limit = 10 } = request.query as any;

      try {
        const consumers = await usageAnalyticsService.getTopResourceConsumers(
          orgId,
          resourceType,
          limit
        );
        return reply.send(consumers);
      } catch (error) {
        fastify.log.error(error);
        return reply.status(400).send({ error: error.message });
      }
    }
  );

  // Get usage trends
  fastify.get(
    '/organizations/:orgId/analytics/trends',
    {
      preHandler: [authenticate, authorize('analytics:view')],
      schema: {
        params: {
          type: 'object',
          properties: {
            orgId: { type: 'string', format: 'uuid' },
          },
          required: ['orgId'],
        },
        querystring: {
          type: 'object',
          properties: {
            resourceType: { type: 'string' },
            days: { type: 'number', default: 30 },
          },
          required: ['resourceType'],
        },
      },
    },
    async (request, reply) => {
      const { orgId } = request.params as { orgId: string };
      const { resourceType, days = 30 } = request.query as any;

      try {
        const trends = await usageAnalyticsService.getUsageTrends(orgId, resourceType, days);
        return reply.send(trends);
      } catch (error) {
        fastify.log.error(error);
        return reply.status(400).send({ error: error.message });
      }
    }
  );
}
```

### Step 5: Testing Strategy

#### 5.1 Unit Tests for Resource Quota Service

```typescript
// packages/orchestrator/src/services/__tests__/resource-quota.service.test.ts
import { describe, it, expect, beforeEach, vi } from 'vitest';
import { ResourceQuotaService } from '../resource-quota.service';
import { db } from '@tamma/database';

vi.mock('@tamma/database');

describe('ResourceQuotaService', () => {
  let resourceQuotaService: ResourceQuotaService;

  beforeEach(() => {
    resourceQuotaService = new ResourceQuotaService();
    vi.clearAllMocks();
  });

  describe('checkQuota', () => {
    it('should allow usage within quota limits', async () => {
      vi.mocked(db.select).mockReturnValue({
        from: vi.fn().mockReturnValue({
          where: vi.fn().mockReturnValue({
            limit: vi.fn().mockResolvedValue([
              {
                id: 'rt-123',
                name: 'api_requests',
                unit: 'requests',
              },
            ]),
          }),
        }),
      } as any);

      vi.mocked(db.select).mockReturnValue({
        from: vi.fn().mockReturnValue({
          where: vi.fn().mockReturnValue({
            limit: vi.fn().mockResolvedValue([
              {
                quotaLimit: 1000,
                isUnlimited: false,
                currentUsage: 100,
                usagePercentage: 10,
                remaining: 900,
              },
            ]),
          }),
        }),
      } as any);

      const result = await resourceQuotaService.checkQuota({
        organizationId: 'org-123',
        resourceType: 'api_requests',
        amount: 100,
      });

      expect(result.allowed).toBe(true);
      expect(result.quota.remaining).toBe(900);
    });

    it('should deny usage that exceeds quota', async () => {
      vi.mocked(db.select).mockReturnValue({
        from: vi.fn().mockReturnValue({
          where: vi.fn().mockReturnValue({
            limit: vi.fn().mockResolvedValue([
              {
                id: 'rt-123',
                name: 'api_requests',
                unit: 'requests',
              },
            ]),
          }),
        }),
      } as any);

      vi.mocked(db.select).mockReturnValue({
        from: vi.fn().mockReturnValue({
          where: vi.fn().mockReturnValue({
            limit: vi.fn().mockResolvedValue([
              {
                quotaLimit: 1000,
                isUnlimited: false,
                currentUsage: 950,
                usagePercentage: 95,
                remaining: 50,
              },
            ]),
          }),
        }),
      } as any);

      const result = await resourceQuotaService.checkQuota({
        organizationId: 'org-123',
        resourceType: 'api_requests',
        amount: 100,
      });

      expect(result.allowed).toBe(false);
      expect(result.reason).toContain('Quota would be exceeded');
    });

    it('should allow unlimited usage for unlimited quotas', async () => {
      vi.mocked(db.select).mockReturnValue({
        from: vi.fn().mockReturnValue({
          where: vi.fn().mockReturnValue({
            limit: vi.fn().mockResolvedValue([
              {
                id: 'rt-123',
                name: 'api_requests',
                unit: 'requests',
              },
            ]),
          }),
        }),
      } as any);

      vi.mocked(db.select).mockReturnValue({
        from: vi.fn().mockReturnValue({
          where: vi.fn().mockReturnValue({
            limit: vi.fn().mockResolvedValue([
              {
                quotaLimit: null,
                isUnlimited: true,
                currentUsage: 10000,
                usagePercentage: 0,
                remaining: null,
              },
            ]),
          }),
        }),
      } as any);

      const result = await resourceQuotaService.checkQuota({
        organizationId: 'org-123',
        resourceType: 'api_requests',
        amount: 1000000,
      });

      expect(result.allowed).toBe(true);
    });
  });

  describe('recordUsage', () => {
    it('should record usage when allowed', async () => {
      vi.mocked(db.transaction).mockImplementation(async (callback) => {
        const mockTx = {
          insert: vi.fn().mockReturnValue({
            values: vi.fn().mockReturnValue({
              onConflictDoUpdate: vi.fn().mockReturnValue({
                set: vi.fn(),
              }),
            }),
          }),
        };
        return callback(mockTx);
      });

      vi.mocked(db.select).mockReturnValue({
        from: vi.fn().mockReturnValue({
          where: vi.fn().mockReturnValue({
            limit: vi.fn().mockResolvedValue([
              {
                id: 'rt-123',
                name: 'api_requests',
              },
            ]),
          }),
        }),
      } as any);

      await resourceQuotaService.recordUsage({
        organizationId: 'org-123',
        resourceType: 'api_requests',
        amount: 100,
        userId: 'user-123',
        allowed: true,
      });

      expect(db.transaction).toHaveBeenCalled();
    });

    it('should not record usage when not allowed', async () => {
      vi.mocked(db.transaction).mockImplementation(async (callback) => {
        const mockTx = {
          insert: vi.fn(),
        };
        return callback(mockTx);
      });

      await resourceQuotaService.recordUsage({
        organizationId: 'org-123',
        resourceType: 'api_requests',
        amount: 100,
        userId: 'user-123',
        allowed: false,
      });

      // Transaction should not be called for disallowed usage
      expect(db.transaction).not.toHaveBeenCalled();
    });
  });
});
```

## Dependencies

### Internal Dependencies

- Database schema and migrations
- Authentication and authorization middleware
- Organization management system
- User management system
- Audit logging system

### External Dependencies

- Payment processing (Stripe, PayPal, etc.)
- Email service for usage alerts
- Time series database for analytics (optional)
- Monitoring and alerting tools

## Risks and Mitigations

### Technical Risks

1. **Performance Impact**: Real-time quota checking could impact API response times
   - Mitigation: Use caching, batch processing, and efficient database queries
2. **Data Consistency**: Race conditions in usage tracking
   - Mitigation: Use database transactions and proper locking
3. **Scalability**: High-volume usage tracking could overwhelm the system
   - Mitigation: Implement event streaming, time-series databases, and data aggregation

### Business Risks

1. **Revenue Leakage**: Inaccurate usage tracking could lead to billing errors
   - Mitigation: Implement reconciliation processes and audit trails
2. **Customer Dissatisfaction**: Aggressive quota enforcement could frustrate users
   - Mitigation: Implement grace periods, clear communication, and flexible limits
3. **Cost Control**: Unlimited resources could lead to cost overruns
   - Mitigation: Implement monitoring, alerts, and automated controls

## Success Metrics

### Functional Metrics

- Quota check response time: <50ms
- Usage tracking accuracy: >99.9%
- Alert delivery rate: >95%
- Report generation time: <5 seconds

### Business Metrics

- Revenue from usage-based billing
- Customer satisfaction with resource limits
- Reduction in resource abuse
- Cost optimization effectiveness

## Rollback Plan

### Database Rollback

```sql
-- Drop resource management tables
DROP TABLE IF EXISTS usage_alerts;
DROP TABLE IF EXISTS invoice_line_items;
DROP TABLE IF EXISTS invoices;
DROP TABLE IF EXISTS billing_information;
DROP TABLE IF EXISTS resource_usage_events;
DROP TABLE IF EXISTS resource_usage;
DROP TABLE IF EXISTS organization_resource_quotas;
DROP TABLE IF EXISTS organization_subscriptions;
DROP TABLE IF EXISTS tier_resource_quotas;
DROP TABLE IF EXISTS subscription_tiers;
DROP TABLE IF EXISTS resource_types;
```

### Code Rollback

- Remove resource management services
- Remove related API routes and middleware
- Restore previous billing and subscription logic

## Completion Checklist

- [ ] Database schema created with proper constraints
- [ ] RLS policies implemented and tested
- [ ] Resource quota service implemented
- [ ] Usage analytics service implemented
- [ ] API endpoints created with proper validation
- [ ] Real-time usage tracking implemented
- [ ] Alert system implemented
- [ ] Unit tests written with >80% coverage
- [ ] Integration tests passing
- [ ] Performance testing completed
- [ ] Security review completed
- [ ] Documentation updated
