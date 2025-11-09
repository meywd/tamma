# Task 4: Team Member Management

**Story**: 1.3 - Organization Management & Multi-Tenancy  
**Task**: 4 - Team Member Management  
**Priority**: High  
**Estimated Time**: 8 hours

## Description

Implement comprehensive team member management functionality including invitations, role assignments, member lifecycle management, and audit trails. This task enables organizations to onboard, manage, and offboard team members with proper role-based access control and complete audit logging.

## Acceptance Criteria

### 1. Invitation System

- [ ] Send email invitations to potential team members
- [ ] Support invitation expiration and re-sending
- [ ] Handle invitation acceptance and account creation
- [ ] Support invitation revocation before acceptance

### 2. Member Lifecycle Management

- [ ] Add members to organizations with specific roles
- [ ] Update member roles and permissions
- [ ] Suspend/activate member accounts
- [ ] Remove members from organizations
- [ ] Handle member transfer between organizations

### 3. Role Assignment Management

- [ ] Assign custom roles to members
- [ ] Support multiple roles per member
- [ ] Implement role hierarchy and inheritance
- [ ] Handle role conflicts and resolution

### 4. Audit and Compliance

- [ ] Log all member management actions
- [ ] Track member activity and login history
- [ ] Generate member activity reports
- [ ] Support compliance audit trails

## Implementation Plan

### Step 1: Database Schema for Team Management

#### 1.1 Create Invitations Table

```sql
-- invitations table
CREATE TABLE invitations (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    email VARCHAR(255) NOT NULL,
    role_id UUID REFERENCES roles(id) ON DELETE SET NULL,
    invited_by UUID NOT NULL REFERENCES users(id),
    token VARCHAR(255) UNIQUE NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'accepted', 'expired', 'revoked')),
    expires_at TIMESTAMPTZ NOT NULL,
    accepted_at TIMESTAMPTZ,
    accepted_by UUID REFERENCES users(id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indexes for invitations
CREATE INDEX idx_invitations_org_id ON invitations(organization_id);
CREATE INDEX idx_invitations_email ON invitations(email);
CREATE INDEX idx_invitations_token ON invitations(token);
CREATE INDEX idx_invitations_status ON invitations(status);
CREATE INDEX idx_invitations_expires_at ON invitations(expires_at);
```

#### 1.2 Create Organization Members Table

```sql
-- organization_members table
CREATE TABLE organization_members (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role_id UUID REFERENCES roles(id) ON DELETE SET NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'suspended', 'pending')),
    invited_by UUID REFERENCES users(id),
    joined_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_login_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    UNIQUE(organization_id, user_id)
);

-- Indexes for organization_members
CREATE INDEX idx_org_members_org_id ON organization_members(organization_id);
CREATE INDEX idx_org_members_user_id ON organization_members(user_id);
CREATE INDEX idx_org_members_role_id ON organization_members(role_id);
CREATE INDEX idx_org_members_status ON organization_members(status);
```

#### 1.3 Create Member Roles Junction Table

```sql
-- member_roles table for multiple roles per member
CREATE TABLE member_roles (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_member_id UUID NOT NULL REFERENCES organization_members(id) ON DELETE CASCADE,
    role_id UUID NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
    assigned_by UUID NOT NULL REFERENCES users(id),
    assigned_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    UNIQUE(organization_member_id, role_id)
);

-- Indexes for member_roles
CREATE INDEX idx_member_roles_member_id ON member_roles(organization_member_id);
CREATE INDEX idx_member_roles_role_id ON member_roles(role_id);
```

#### 1.4 Create Member Activity Log Table

```sql
-- member_activity_log table
CREATE TABLE member_activity_log (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    action VARCHAR(50) NOT NULL,
    resource_type VARCHAR(50),
    resource_id UUID,
    details JSONB,
    ip_address INET,
    user_agent TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indexes for member_activity_log
CREATE INDEX idx_member_activity_org_id ON member_activity_log(organization_id);
CREATE INDEX idx_member_activity_user_id ON member_activity_log(user_id);
CREATE INDEX idx_member_activity_action ON member_activity_log(action);
CREATE INDEX idx_member_activity_created_at ON member_activity_log(created_at);
```

### Step 2: RLS Policies for Team Management

#### 2.1 Invitations RLS Policies

```sql
-- Enable RLS on invitations
ALTER TABLE invitations ENABLE ROW LEVEL SECURITY;

-- Policy for organization owners/managers
CREATE POLICY invitations_org_management ON invitations
    FOR ALL
    TO authenticated
    USING (
        organization_id IN (
            SELECT id FROM organizations
            WHERE id = invitations.organization_id
            AND owner_id = auth.uid()
        )
        OR
        EXISTS (
            SELECT 1 FROM organization_members om
            JOIN member_roles mr ON om.id = mr.organization_member_id
            JOIN roles r ON mr.role_id = r.id
            WHERE om.organization_id = invitations.organization_id
            AND om.user_id = auth.uid()
            AND om.status = 'active'
            AND r.permissions @> '["member:invite"]'
        )
    );

-- Policy for invited users (view own invitations)
CREATE POLICY invitations_view_own ON invitations
    FOR SELECT
    TO authenticated
    USING (email = (SELECT email FROM users WHERE id = auth.uid()));
```

#### 2.2 Organization Members RLS Policies

```sql
-- Enable RLS on organization_members
ALTER TABLE organization_members ENABLE ROW LEVEL SECURITY;

-- Policy for organization management
CREATE POLICY org_members_management ON organization_members
    FOR ALL
    TO authenticated
    USING (
        organization_id IN (
            SELECT id FROM organizations WHERE owner_id = auth.uid()
        )
        OR
        EXISTS (
            SELECT 1 FROM organization_members om
            JOIN member_roles mr ON om.id = mr.organization_member_id
            JOIN roles r ON mr.role_id = r.id
            WHERE om.organization_id = organization_members.organization_id
            AND om.user_id = auth.uid()
            AND om.status = 'active'
            AND r.permissions @> '["member:manage"]'
        )
    );

-- Policy for members to view their own membership
CREATE POLICY org_members_view_own ON organization_members
    FOR SELECT
    TO authenticated
    USING (user_id = auth.uid());
```

#### 2.3 Member Activity Log RLS Policies

```sql
-- Enable RLS on member_activity_log
ALTER TABLE member_activity_log ENABLE ROW LEVEL SECURITY;

-- Policy for viewing organization activity
CREATE POLICY member_activity_view_org ON member_activity_log
    FOR SELECT
    TO authenticated
    USING (
        organization_id IN (
            SELECT id FROM organizations WHERE owner_id = auth.uid()
        )
        OR
        EXISTS (
            SELECT 1 FROM organization_members om
            JOIN member_roles mr ON om.id = mr.organization_member_id
            JOIN roles r ON mr.role_id = r.id
            WHERE om.organization_id = member_activity_log.organization_id
            AND om.user_id = auth.uid()
            AND om.status = 'active'
            AND r.permissions @> '["audit:view"]'
        )
    );

-- Policy for viewing own activity
CREATE POLICY member_activity_view_own ON member_activity_log
    FOR SELECT
    TO authenticated
    USING (user_id = auth.uid());
```

### Step 3: Team Management Services

#### 3.1 Invitation Service

```typescript
// packages/orchestrator/src/services/invitation.service.ts
import { db } from '@tamma/database';
import { nanoid } from 'nanoid';
import dayjs from 'dayjs';
import { emailService } from './email.service';
import { auditService } from './audit.service';

export interface CreateInvitationRequest {
  organizationId: string;
  email: string;
  roleId?: string;
  message?: string;
}

export interface InvitationResponse {
  id: string;
  organizationId: string;
  email: string;
  status: string;
  expiresAt: string;
  createdAt: string;
}

export class InvitationService {
  async createInvitation(
    request: CreateInvitationRequest,
    inviterId: string
  ): Promise<InvitationResponse> {
    const { organizationId, email, roleId, message } = request;

    // Check if user is already a member
    const existingMember = await db
      .select()
      .from(organizationMembers)
      .where(
        and(
          eq(organizationMembers.organizationId, organizationId),
          eq(organizationMembers.email, email)
        )
      )
      .limit(1);

    if (existingMember.length > 0) {
      throw new Error('User is already a member of this organization');
    }

    // Check for pending invitation
    const existingInvitation = await db
      .select()
      .from(invitations)
      .where(
        and(
          eq(invitations.organizationId, organizationId),
          eq(invitations.email, email),
          eq(invitations.status, 'pending')
        )
      )
      .limit(1);

    if (existingInvitation.length > 0) {
      throw new Error('Invitation already sent to this email');
    }

    // Generate invitation token and expiration
    const token = nanoid(32);
    const expiresAt = dayjs().add(7, 'days').toDate();

    // Create invitation
    const [invitation] = await db
      .insert(invitations)
      .values({
        organizationId,
        email,
        roleId,
        invitedBy: inviterId,
        token,
        status: 'pending',
        expiresAt,
      })
      .returning();

    // Send invitation email
    await emailService.sendInvitationEmail({
      to: email,
      organizationId,
      token,
      message,
    });

    // Log invitation creation
    await auditService.logEvent({
      organizationId,
      userId: inviterId,
      action: 'invitation.created',
      resourceType: 'invitation',
      resourceId: invitation.id,
      details: {
        email,
        roleId,
        expiresAt: invitation.expiresAt,
      },
    });

    return {
      id: invitation.id,
      organizationId: invitation.organizationId,
      email: invitation.email,
      status: invitation.status,
      expiresAt: invitation.expiresAt.toISOString(),
      createdAt: invitation.createdAt.toISOString(),
    };
  }

  async acceptInvitation(token: string, userId: string): Promise<void> {
    const [invitation] = await db
      .select()
      .from(invitations)
      .where(eq(invitations.token, token))
      .limit(1);

    if (!invitation) {
      throw new Error('Invalid invitation token');
    }

    if (invitation.status !== 'pending') {
      throw new Error('Invitation is no longer valid');
    }

    if (dayjs().isAfter(invitation.expiresAt)) {
      await db
        .update(invitations)
        .set({ status: 'expired' })
        .where(eq(invitations.id, invitation.id));

      throw new Error('Invitation has expired');
    }

    // Add user to organization
    await db.transaction(async (tx) => {
      // Create organization member
      const [member] = await tx
        .insert(organizationMembers)
        .values({
          organizationId: invitation.organizationId,
          userId,
          roleId: invitation.roleId,
          status: 'active',
          invitedBy: invitation.invitedBy,
        })
        .returning();

      // Assign role if specified
      if (invitation.roleId) {
        await tx.insert(memberRoles).values({
          organizationMemberId: member.id,
          roleId: invitation.roleId,
          assignedBy: invitation.invitedBy,
        });
      }

      // Update invitation status
      await tx
        .update(invitations)
        .set({
          status: 'accepted',
          acceptedAt: new Date(),
          acceptedBy: userId,
        })
        .where(eq(invitations.id, invitation.id));
    });

    // Log invitation acceptance
    await auditService.logEvent({
      organizationId: invitation.organizationId,
      userId,
      action: 'invitation.accepted',
      resourceType: 'invitation',
      resourceId: invitation.id,
      details: {
        email: invitation.email,
        invitedBy: invitation.invitedBy,
      },
    });
  }

  async revokeInvitation(invitationId: string, revokedBy: string): Promise<void> {
    const [invitation] = await db
      .select()
      .from(invitations)
      .where(eq(invitations.id, invitationId))
      .limit(1);

    if (!invitation) {
      throw new Error('Invitation not found');
    }

    if (invitation.status !== 'pending') {
      throw new Error('Cannot revoke non-pending invitation');
    }

    await db.update(invitations).set({ status: 'revoked' }).where(eq(invitations.id, invitationId));

    // Log invitation revocation
    await auditService.logEvent({
      organizationId: invitation.organizationId,
      userId: revokedBy,
      action: 'invitation.revoked',
      resourceType: 'invitation',
      resourceId: invitation.id,
      details: {
        email: invitation.email,
        previousStatus: invitation.status,
      },
    });
  }

  async resendInvitation(invitationId: string, resentBy: string): Promise<void> {
    const [invitation] = await db
      .select()
      .from(invitations)
      .where(eq(invitations.id, invitationId))
      .limit(1);

    if (!invitation) {
      throw new Error('Invitation not found');
    }

    if (invitation.status !== 'pending') {
      throw new Error('Cannot resend non-pending invitation');
    }

    // Update expiration
    const newExpiresAt = dayjs().add(7, 'days').toDate();

    await db
      .update(invitations)
      .set({ expiresAt: newExpiresAt })
      .where(eq(invitations.id, invitationId));

    // Resend email
    await emailService.sendInvitationEmail({
      to: invitation.email,
      organizationId: invitation.organizationId,
      token: invitation.token,
    });

    // Log invitation resend
    await auditService.logEvent({
      organizationId: invitation.organizationId,
      userId: resentBy,
      action: 'invitation.resent',
      resourceType: 'invitation',
      resourceId: invitation.id,
      details: {
        email: invitation.email,
        newExpiresAt: newExpiresAt.toISOString(),
      },
    });
  }

  async getInvitations(organizationId: string): Promise<InvitationResponse[]> {
    const invitations = await db
      .select()
      .from(invitations)
      .where(eq(invitations.organizationId, organizationId))
      .orderBy(desc(invitations.createdAt));

    return invitations.map((invitation) => ({
      id: invitation.id,
      organizationId: invitation.organizationId,
      email: invitation.email,
      status: invitation.status,
      expiresAt: invitation.expiresAt.toISOString(),
      createdAt: invitation.createdAt.toISOString(),
    }));
  }
}
```

#### 3.2 Member Management Service

```typescript
// packages/orchestrator/src/services/member.service.ts
import { db } from '@tamma/database';
import { eq, and, desc } from 'drizzle-orm';
import { auditService } from './audit.service';

export interface AddMemberRequest {
  organizationId: string;
  userId: string;
  roleIds?: string[];
}

export interface UpdateMemberRequest {
  roleIds?: string[];
  status?: 'active' | 'suspended';
}

export interface MemberResponse {
  id: string;
  organizationId: string;
  userId: string;
  email: string;
  name: string;
  status: string;
  roles: Array<{
    id: string;
    name: string;
    permissions: string[];
  }>;
  joinedAt: string;
  lastLoginAt?: string;
}

export class MemberService {
  async addMember(request: AddMemberRequest, addedBy: string): Promise<MemberResponse> {
    const { organizationId, userId, roleIds = [] } = request;

    // Check if user exists
    const [user] = await db.select().from(users).where(eq(users.id, userId)).limit(1);

    if (!user) {
      throw new Error('User not found');
    }

    // Check if already a member
    const [existingMember] = await db
      .select()
      .from(organizationMembers)
      .where(
        and(
          eq(organizationMembers.organizationId, organizationId),
          eq(organizationMembers.userId, userId)
        )
      )
      .limit(1);

    if (existingMember) {
      throw new Error('User is already a member of this organization');
    }

    // Add member and roles
    const [member] = await db.transaction(async (tx) => {
      // Create member
      const [newMember] = await tx
        .insert(organizationMembers)
        .values({
          organizationId,
          userId,
          status: 'active',
          invitedBy: addedBy,
        })
        .returning();

      // Assign roles
      if (roleIds.length > 0) {
        await tx.insert(memberRoles).values(
          roleIds.map((roleId) => ({
            organizationMemberId: newMember.id,
            roleId,
            assignedBy: addedBy,
          }))
        );
      }

      return newMember;
    });

    // Log member addition
    await auditService.logEvent({
      organizationId,
      userId: addedBy,
      action: 'member.added',
      resourceType: 'member',
      resourceId: member.id,
      details: {
        targetUserId: userId,
        roleIds,
      },
    });

    return this.getMemberById(member.id);
  }

  async updateMember(
    memberId: string,
    request: UpdateMemberRequest,
    updatedBy: string
  ): Promise<MemberResponse> {
    const [member] = await db
      .select()
      .from(organizationMembers)
      .where(eq(organizationMembers.id, memberId))
      .limit(1);

    if (!member) {
      throw new Error('Member not found');
    }

    await db.transaction(async (tx) => {
      // Update member status if provided
      if (request.status) {
        await tx
          .update(organizationMembers)
          .set({ status: request.status })
          .where(eq(organizationMembers.id, memberId));
      }

      // Update roles if provided
      if (request.roleIds !== undefined) {
        // Remove existing roles
        await tx.delete(memberRoles).where(eq(memberRoles.organizationMemberId, memberId));

        // Add new roles
        if (request.roleIds.length > 0) {
          await tx.insert(memberRoles).values(
            request.roleIds.map((roleId) => ({
              organizationMemberId: memberId,
              roleId,
              assignedBy: updatedBy,
            }))
          );
        }
      }
    });

    // Log member update
    await auditService.logEvent({
      organizationId: member.organizationId,
      userId: updatedBy,
      action: 'member.updated',
      resourceType: 'member',
      resourceId: memberId,
      details: {
        targetUserId: member.userId,
        changes: request,
      },
    });

    return this.getMemberById(memberId);
  }

  async removeMember(memberId: string, removedBy: string): Promise<void> {
    const [member] = await db
      .select()
      .from(organizationMembers)
      .where(eq(organizationMembers.id, memberId))
      .limit(1);

    if (!member) {
      throw new Error('Member not found');
    }

    // Cannot remove the owner
    const [org] = await db
      .select()
      .from(organizations)
      .where(eq(organizations.id, member.organizationId))
      .limit(1);

    if (org.ownerId === member.userId) {
      throw new Error('Cannot remove organization owner');
    }

    await db.transaction(async (tx) => {
      // Remove member roles
      await tx.delete(memberRoles).where(eq(memberRoles.organizationMemberId, memberId));

      // Remove member
      await tx.delete(organizationMembers).where(eq(organizationMembers.id, memberId));
    });

    // Log member removal
    await auditService.logEvent({
      organizationId: member.organizationId,
      userId: removedBy,
      action: 'member.removed',
      resourceType: 'member',
      resourceId: memberId,
      details: {
        targetUserId: member.userId,
      },
    });
  }

  async getMembers(organizationId: string): Promise<MemberResponse[]> {
    const members = await db
      .select({
        member: organizationMembers,
        user: users,
      })
      .from(organizationMembers)
      .innerJoin(users, eq(organizationMembers.userId, users.id))
      .where(eq(organizationMembers.organizationId, organizationId))
      .orderBy(desc(organizationMembers.createdAt));

    // Get roles for each member
    const memberIds = members.map((m) => m.member.id);
    const roles = await db
      .select({
        memberRoleId: memberRoles.organizationMemberId,
        role: roles,
      })
      .from(memberRoles)
      .innerJoin(roles, eq(memberRoles.roleId, roles.id))
      .where(memberRoles.organizationMemberId.in(memberIds));

    // Group roles by member
    const rolesByMember = roles.reduce(
      (acc, role) => {
        if (!acc[role.memberRoleId]) {
          acc[role.memberRoleId] = [];
        }
        acc[role.memberRoleId].push({
          id: role.role.id,
          name: role.role.name,
          permissions: role.role.permissions,
        });
        return acc;
      },
      {} as Record<string, Array<{ id: string; name: string; permissions: string[] }>>
    );

    return members.map(({ member, user }) => ({
      id: member.id,
      organizationId: member.organizationId,
      userId: member.userId,
      email: user.email,
      name: user.name,
      status: member.status,
      roles: rolesByMember[member.id] || [],
      joinedAt: member.joinedAt.toISOString(),
      lastLoginAt: member.lastLoginAt?.toISOString(),
    }));
  }

  async getMemberById(memberId: string): Promise<MemberResponse> {
    const [member] = await db
      .select({
        member: organizationMembers,
        user: users,
      })
      .from(organizationMembers)
      .innerJoin(users, eq(organizationMembers.userId, users.id))
      .where(eq(organizationMembers.id, memberId))
      .limit(1);

    if (!member) {
      throw new Error('Member not found');
    }

    // Get member roles
    const memberRoles = await db
      .select({
        role: roles,
      })
      .from(memberRoles)
      .innerJoin(roles, eq(memberRoles.roleId, roles.id))
      .where(eq(memberRoles.organizationMemberId, memberId));

    return {
      id: member.member.id,
      organizationId: member.member.organizationId,
      userId: member.member.userId,
      email: member.user.email,
      name: member.user.name,
      status: member.member.status,
      roles: memberRoles.map((r) => ({
        id: r.role.id,
        name: r.role.name,
        permissions: r.role.permissions,
      })),
      joinedAt: member.member.joinedAt.toISOString(),
      lastLoginAt: member.member.lastLoginAt?.toISOString(),
    };
  }

  async updateLastLogin(memberId: string): Promise<void> {
    await db
      .update(organizationMembers)
      .set({ lastLoginAt: new Date() })
      .where(eq(organizationMembers.id, memberId));
  }
}
```

### Step 4: API Endpoints for Team Management

#### 4.1 Invitation Endpoints

```typescript
// packages/api/src/routes/invitations.ts
import { FastifyInstance } from 'fastify';
import { invitationService } from '@tamma/orchestrator/services';
import { authenticate, authorize } from '../middleware/auth';

export default async function invitationRoutes(fastify: FastifyInstance) {
  // Create invitation
  fastify.post(
    '/organizations/:orgId/invitations',
    {
      preHandler: [authenticate, authorize('member:invite')],
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
            email: { type: 'string', format: 'email' },
            roleId: { type: 'string', format: 'uuid' },
            message: { type: 'string' },
          },
          required: ['email'],
        },
      },
    },
    async (request, reply) => {
      const { orgId } = request.params as { orgId: string };
      const { email, roleId, message } = request.body as any;
      const userId = request.user.id;

      try {
        const invitation = await invitationService.createInvitation(
          {
            organizationId: orgId,
            email,
            roleId,
            message,
          },
          userId
        );

        return reply.status(201).send(invitation);
      } catch (error) {
        fastify.log.error(error);
        return reply.status(400).send({ error: error.message });
      }
    }
  );

  // Get invitations
  fastify.get(
    '/organizations/:orgId/invitations',
    {
      preHandler: [authenticate, authorize('member:view')],
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
        const invitations = await invitationService.getInvitations(orgId);
        return reply.send(invitations);
      } catch (error) {
        fastify.log.error(error);
        return reply.status(400).send({ error: error.message });
      }
    }
  );

  // Resend invitation
  fastify.post(
    '/invitations/:invitationId/resend',
    {
      preHandler: [authenticate, authorize('member:invite')],
      schema: {
        params: {
          type: 'object',
          properties: {
            invitationId: { type: 'string', format: 'uuid' },
          },
          required: ['invitationId'],
        },
      },
    },
    async (request, reply) => {
      const { invitationId } = request.params as { invitationId: string };
      const userId = request.user.id;

      try {
        await invitationService.resendInvitation(invitationId, userId);
        return reply.send({ message: 'Invitation resent successfully' });
      } catch (error) {
        fastify.log.error(error);
        return reply.status(400).send({ error: error.message });
      }
    }
  );

  // Revoke invitation
  fastify.delete(
    '/invitations/:invitationId',
    {
      preHandler: [authenticate, authorize('member:invite')],
      schema: {
        params: {
          type: 'object',
          properties: {
            invitationId: { type: 'string', format: 'uuid' },
          },
          required: ['invitationId'],
        },
      },
    },
    async (request, reply) => {
      const { invitationId } = request.params as { invitationId: string };
      const userId = request.user.id;

      try {
        await invitationService.revokeInvitation(invitationId, userId);
        return reply.send({ message: 'Invitation revoked successfully' });
      } catch (error) {
        fastify.log.error(error);
        return reply.status(400).send({ error: error.message });
      }
    }
  );

  // Accept invitation (public endpoint)
  fastify.post(
    '/invitations/accept',
    {
      schema: {
        body: {
          type: 'object',
          properties: {
            token: { type: 'string' },
          },
          required: ['token'],
        },
      },
    },
    async (request, reply) => {
      const { token } = request.body as any;
      const userId = request.user?.id;

      if (!userId) {
        return reply.status(401).send({ error: 'Authentication required' });
      }

      try {
        await invitationService.acceptInvitation(token, userId);
        return reply.send({ message: 'Invitation accepted successfully' });
      } catch (error) {
        fastify.log.error(error);
        return reply.status(400).send({ error: error.message });
      }
    }
  );
}
```

#### 4.2 Member Management Endpoints

```typescript
// packages/api/src/routes/members.ts
import { FastifyInstance } from 'fastify';
import { memberService } from '@tamma/orchestrator/services';
import { authenticate, authorize } from '../middleware/auth';

export default async function memberRoutes(fastify: FastifyInstance) {
  // Get members
  fastify.get(
    '/organizations/:orgId/members',
    {
      preHandler: [authenticate, authorize('member:view')],
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
        const members = await memberService.getMembers(orgId);
        return reply.send(members);
      } catch (error) {
        fastify.log.error(error);
        return reply.status(400).send({ error: error.message });
      }
    }
  );

  // Add member
  fastify.post(
    '/organizations/:orgId/members',
    {
      preHandler: [authenticate, authorize('member:add')],
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
            userId: { type: 'string', format: 'uuid' },
            roleIds: {
              type: 'array',
              items: { type: 'string', format: 'uuid' },
            },
          },
          required: ['userId'],
        },
      },
    },
    async (request, reply) => {
      const { orgId } = request.params as { orgId: string };
      const { userId, roleIds } = request.body as any;
      const currentUserId = request.user.id;

      try {
        const member = await memberService.addMember(
          {
            organizationId: orgId,
            userId,
            roleIds,
          },
          currentUserId
        );

        return reply.status(201).send(member);
      } catch (error) {
        fastify.log.error(error);
        return reply.status(400).send({ error: error.message });
      }
    }
  );

  // Update member
  fastify.patch(
    '/members/:memberId',
    {
      preHandler: [authenticate, authorize('member:manage')],
      schema: {
        params: {
          type: 'object',
          properties: {
            memberId: { type: 'string', format: 'uuid' },
          },
          required: ['memberId'],
        },
        body: {
          type: 'object',
          properties: {
            roleIds: {
              type: 'array',
              items: { type: 'string', format: 'uuid' },
            },
            status: { type: 'string', enum: ['active', 'suspended'] },
          },
        },
      },
    },
    async (request, reply) => {
      const { memberId } = request.params as { memberId: string };
      const updates = request.body as any;
      const currentUserId = request.user.id;

      try {
        const member = await memberService.updateMember(memberId, updates, currentUserId);

        return reply.send(member);
      } catch (error) {
        fastify.log.error(error);
        return reply.status(400).send({ error: error.message });
      }
    }
  );

  // Remove member
  fastify.delete(
    '/members/:memberId',
    {
      preHandler: [authenticate, authorize('member:remove')],
      schema: {
        params: {
          type: 'object',
          properties: {
            memberId: { type: 'string', format: 'uuid' },
          },
          required: ['memberId'],
        },
      },
    },
    async (request, reply) => {
      const { memberId } = request.params as { memberId: string };
      const currentUserId = request.user.id;

      try {
        await memberService.removeMember(memberId, currentUserId);
        return reply.send({ message: 'Member removed successfully' });
      } catch (error) {
        fastify.log.error(error);
        return reply.status(400).send({ error: error.message });
      }
    }
  );

  // Get member by ID
  fastify.get(
    '/members/:memberId',
    {
      preHandler: [authenticate, authorize('member:view')],
      schema: {
        params: {
          type: 'object',
          properties: {
            memberId: { type: 'string', format: 'uuid' },
          },
          required: ['memberId'],
        },
      },
    },
    async (request, reply) => {
      const { memberId } = request.params as { memberId: string };

      try {
        const member = await memberService.getMemberById(memberId);
        return reply.send(member);
      } catch (error) {
        fastify.log.error(error);
        return reply.status(404).send({ error: 'Member not found' });
      }
    }
  );
}
```

### Step 5: Testing Strategy

#### 5.1 Unit Tests for Invitation Service

```typescript
// packages/orchestrator/src/services/__tests__/invitation.service.test.ts
import { describe, it, expect, beforeEach, vi } from 'vitest';
import { InvitationService } from '../invitation.service';
import { db } from '@tamma/database';
import { emailService } from '../email.service';
import { auditService } from '../audit.service';

vi.mock('@tamma/database');
vi.mock('../email.service');
vi.mock('../audit.service');

describe('InvitationService', () => {
  let invitationService: InvitationService;

  beforeEach(() => {
    invitationService = new InvitationService();
    vi.clearAllMocks();
  });

  describe('createInvitation', () => {
    it('should create invitation successfully', async () => {
      const mockInvitation = {
        id: 'inv-123',
        organizationId: 'org-123',
        email: 'test@example.com',
        status: 'pending',
        expiresAt: new Date(),
        createdAt: new Date(),
      };

      vi.mocked(db.select).mockReturnValue({
        where: vi.fn().mockReturnValue({
          limit: vi.fn().mockResolvedValue([]),
        }),
      } as any);

      vi.mocked(db.insert).mockReturnValue({
        values: vi.fn().mockReturnValue({
          returning: vi.fn().mockResolvedValue([mockInvitation]),
        }),
      } as any);

      vi.mocked(emailService.sendInvitationEmail).mockResolvedValue(undefined);
      vi.mocked(auditService.logEvent).mockResolvedValue(undefined);

      const result = await invitationService.createInvitation(
        {
          organizationId: 'org-123',
          email: 'test@example.com',
          roleId: 'role-123',
        },
        'user-123'
      );

      expect(result).toEqual({
        id: 'inv-123',
        organizationId: 'org-123',
        email: 'test@example.com',
        status: 'pending',
        expiresAt: mockInvitation.expiresAt.toISOString(),
        createdAt: mockInvitation.createdAt.toISOString(),
      });

      expect(emailService.sendInvitationEmail).toHaveBeenCalled();
      expect(auditService.logEvent).toHaveBeenCalledWith({
        organizationId: 'org-123',
        userId: 'user-123',
        action: 'invitation.created',
        resourceType: 'invitation',
        resourceId: 'inv-123',
        details: {
          email: 'test@example.com',
          roleId: 'role-123',
          expiresAt: mockInvitation.expiresAt,
        },
      });
    });

    it('should throw error if user is already a member', async () => {
      vi.mocked(db.select).mockReturnValue({
        where: vi.fn().mockReturnValue({
          limit: vi.fn().mockResolvedValue([{}]), // Existing member
        }),
      } as any);

      await expect(
        invitationService.createInvitation(
          {
            organizationId: 'org-123',
            email: 'test@example.com',
          },
          'user-123'
        )
      ).rejects.toThrow('User is already a member of this organization');
    });

    it('should throw error if invitation already exists', async () => {
      vi.mocked(db.select)
        .mockReturnValueOnce({
          where: vi.fn().mockReturnValue({
            limit: vi.fn().mockResolvedValue([]), // No existing member
          }),
        } as any)
        .mockReturnValueOnce({
          where: vi.fn().mockReturnValue({
            limit: vi.fn().mockResolvedValue([{}]), // Existing invitation
          }),
        } as any);

      await expect(
        invitationService.createInvitation(
          {
            organizationId: 'org-123',
            email: 'test@example.com',
          },
          'user-123'
        )
      ).rejects.toThrow('Invitation already sent to this email');
    });
  });

  describe('acceptInvitation', () => {
    it('should accept invitation successfully', async () => {
      const mockInvitation = {
        id: 'inv-123',
        organizationId: 'org-123',
        email: 'test@example.com',
        status: 'pending',
        expiresAt: new Date(Date.now() + 86400000), // Tomorrow
        invitedBy: 'user-123',
      };

      vi.mocked(db.select).mockReturnValue({
        where: vi.fn().mockReturnValue({
          limit: vi.fn().mockResolvedValue([mockInvitation]),
        }),
      } as any);

      vi.mocked(db.transaction).mockImplementation(async (callback) => {
        return callback({
          insert: vi.fn().mockReturnValue({
            values: vi.fn().mockReturnValue({
              returning: vi.fn().mockResolvedValue([{ id: 'member-123' }]),
            }),
          }),
          update: vi.fn().mockReturnValue({
            set: vi.fn().mockReturnValue({
              where: vi.fn().mockResolvedValue(undefined),
            }),
          }),
        });
      });

      vi.mocked(auditService.logEvent).mockResolvedValue(undefined);

      await invitationService.acceptInvitation('token-123', 'user-456');

      expect(auditService.logEvent).toHaveBeenCalledWith({
        organizationId: 'org-123',
        userId: 'user-456',
        action: 'invitation.accepted',
        resourceType: 'invitation',
        resourceId: 'inv-123',
        details: {
          email: 'test@example.com',
          invitedBy: 'user-123',
        },
      });
    });

    it('should throw error for invalid token', async () => {
      vi.mocked(db.select).mockReturnValue({
        where: vi.fn().mockReturnValue({
          limit: vi.fn().mockResolvedValue([]),
        }),
      } as any);

      await expect(invitationService.acceptInvitation('invalid-token', 'user-123')).rejects.toThrow(
        'Invalid invitation token'
      );
    });

    it('should throw error for expired invitation', async () => {
      const mockInvitation = {
        id: 'inv-123',
        status: 'pending',
        expiresAt: new Date(Date.now() - 86400000), // Yesterday
      };

      vi.mocked(db.select).mockReturnValue({
        where: vi.fn().mockReturnValue({
          limit: vi.fn().mockResolvedValue([mockInvitation]),
        }),
      } as any);

      vi.mocked(db.update).mockReturnValue({
        set: vi.fn().mockReturnValue({
          where: vi.fn().mockResolvedValue(undefined),
        }),
      } as any);

      await expect(invitationService.acceptInvitation('token-123', 'user-123')).rejects.toThrow(
        'Invitation has expired'
      );
    });
  });
});
```

#### 5.2 Integration Tests for Team Management

```typescript
// packages/api/src/routes/__tests__/invitations.integration.test.ts
import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { FastifyInstance } from 'fastify';
import { buildApp } from '../../app';
import { createTestUser, createTestOrganization, createTestRole } from '../../../test/helpers';

describe('Invitation Routes Integration', () => {
  let app: FastifyInstance;
  let authToken: string;
  let organizationId: string;
  let roleId: string;

  beforeEach(async () => {
    app = buildApp();

    // Create test user and organization
    const user = await createTestUser();
    const organization = await createTestOrganization(user.id);
    const role = await createTestRole(organization.id, ['member:invite']);

    authToken = generateTestToken(user.id);
    organizationId = organization.id;
    roleId = role.id;
  });

  afterEach(async () => {
    await app.close();
  });

  describe('POST /organizations/:orgId/invitations', () => {
    it('should create invitation successfully', async () => {
      const response = await app.inject({
        method: 'POST',
        url: `/organizations/${organizationId}/invitations`,
        headers: {
          authorization: `Bearer ${authToken}`,
        },
        payload: {
          email: 'newmember@example.com',
          roleId,
        },
      });

      expect(response.statusCode).toBe(201);
      const invitation = JSON.parse(response.payload);
      expect(invitation.email).toBe('newmember@example.com');
      expect(invitation.status).toBe('pending');
    });

    it('should require authentication', async () => {
      const response = await app.inject({
        method: 'POST',
        url: `/organizations/${organizationId}/invitations`,
        payload: {
          email: 'newmember@example.com',
        },
      });

      expect(response.statusCode).toBe(401);
    });

    it('should require authorization', async () => {
      // Create user without invite permission
      const regularUser = await createTestUser();
      const regularToken = generateTestToken(regularUser.id);

      const response = await app.inject({
        method: 'POST',
        url: `/organizations/${organizationId}/invitations`,
        headers: {
          authorization: `Bearer ${regularToken}`,
        },
        payload: {
          email: 'newmember@example.com',
        },
      });

      expect(response.statusCode).toBe(403);
    });
  });

  describe('GET /organizations/:orgId/invitations', () => {
    it('should get invitations successfully', async () => {
      // Create an invitation first
      await app.inject({
        method: 'POST',
        url: `/organizations/${organizationId}/invitations`,
        headers: {
          authorization: `Bearer ${authToken}`,
        },
        payload: {
          email: 'newmember@example.com',
        },
      });

      const response = await app.inject({
        method: 'GET',
        url: `/organizations/${organizationId}/invitations`,
        headers: {
          authorization: `Bearer ${authToken}`,
        },
      });

      expect(response.statusCode).toBe(200);
      const invitations = JSON.parse(response.payload);
      expect(invitations).toHaveLength(1);
      expect(invitations[0].email).toBe('newmember@example.com');
    });
  });
});
```

## Dependencies

### Internal Dependencies

- Database schema and migrations
- Authentication and authorization middleware
- Email service for invitation notifications
- Audit service for activity logging
- Role-based access control system

### External Dependencies

- Email service provider (SendGrid, AWS SES, etc.)
- UUID generation (nanoid)
- Date/time handling (dayjs)

## Risks and Mitigations

### Technical Risks

1. **Invitation Token Security**: Use cryptographically secure tokens with sufficient entropy
2. **Email Delivery**: Implement retry logic and fallback email providers
3. **Race Conditions**: Use database transactions for invitation acceptance
4. **Permission Escalation**: Strict validation of role assignments and permissions

### Business Risks

1. **Invitation Spam**: Implement rate limiting and invitation quotas
2. **Orphaned Accounts**: Automatic cleanup of expired invitations
3. **Compliance**: Complete audit trail for all member management actions

## Success Metrics

### Functional Metrics

- Invitation creation success rate: >99%
- Email delivery rate: >95%
- Invitation acceptance rate: >80%
- Member management API response time: <200ms

### Security Metrics

- Zero unauthorized access attempts
- Complete audit trail coverage
- No privilege escalation vulnerabilities
- All invitations properly expired

## Rollback Plan

### Database Rollback

```sql
-- Drop team management tables
DROP TABLE IF EXISTS member_activity_log;
DROP TABLE IF EXISTS member_roles;
DROP TABLE IF EXISTS organization_members;
DROP TABLE IF EXISTS invitations;
```

### Code Rollback

- Remove invitation and member management services
- Remove related API routes and middleware
- Restore previous authentication flow

## Completion Checklist

- [ ] Database schema created with proper constraints
- [ ] RLS policies implemented and tested
- [ ] Invitation service implemented with full functionality
- [ ] Member management service implemented
- [ ] API endpoints created with proper validation
- [ ] Email service integration completed
- [ ] Audit logging implemented for all actions
- [ ] Unit tests written with >80% coverage
- [ ] Integration tests passing
- [ ] Security review completed
- [ ] Documentation updated
- [ ] Performance testing completed
