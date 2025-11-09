# Implementation Plan: Task 3 - Role-Based Access Control

**Story**: 1.3 Organization Management & Multi-Tenancy  
**Task**: 3 - Role-Based Access Control  
**Acceptance Criteria**: #2 - Role-based access control (Admin, Developer, Viewer)

## Overview

Implement comprehensive RBAC system with organization roles, permission management, and role hierarchy.

## Implementation Steps

### Subtask 3.1: Define organization roles and permissions

**Objective**: Create detailed role and permission definitions for organizations

**File**: `src/models/organization-roles.ts`

```typescript
export enum OrganizationRole {
  OWNER = 'owner',
  ADMIN = 'admin',
  DEVELOPER = 'developer',
  VIEWER = 'viewer',
}

export enum OrganizationPermission {
  // Organization Management
  ORG_READ = 'org.read',
  ORG_UPDATE = 'org.update',
  ORG_DELETE = 'org.delete',
  ORG_MANAGE_SETTINGS = 'org.manage_settings',
  ORG_MANAGE_BILLING = 'org.manage_billing',
  ORG_MANAGE_SUBSCRIPTION = 'org.manage_subscription',
  ORG_VIEW_AUDIT_LOGS = 'org.view_audit_logs',
  ORG_MANAGE_INTEGRATIONS = 'org.manage_integrations',
  ORG_MANAGE_BRANDING = 'org.manage_branding',

  // Member Management
  MEMBER_INVITE = 'member.invite',
  MEMBER_READ = 'member.read',
  MEMBER_UPDATE = 'member.update',
  MEMBER_REMOVE = 'member.remove',
  MEMBER_MANAGE_ROLES = 'member.manage_roles',
  MEMBER_DEACTIVATE = 'member.deactivate',

  // Project Management
  PROJECT_CREATE = 'project.create',
  PROJECT_READ = 'project.read',
  PROJECT_UPDATE = 'project.update',
  PROJECT_DELETE = 'project.delete',
  PROJECT_MANAGE_MEMBERS = 'project.manage_members',
  PROJECT_ARCHIVE = 'project.archive',
  PROJECT_EXPORT = 'project.export',

  // Benchmark Management
  BENCHMARK_CREATE = 'benchmark.create',
  BENCHMARK_READ = 'benchmark.read',
  BENCHMARK_UPDATE = 'benchmark.update',
  BENCHMARK_DELETE = 'benchmark.delete',
  BENCHMARK_RUN = 'benchmark.run',
  BENCHMARK_VIEW_RESULTS = 'benchmark.view_results',
  BENCHMARK_EXPORT = 'benchmark.export',
  BENCHMARK_SHARE = 'benchmark.share',

  // Dataset Management
  DATASET_UPLOAD = 'dataset.upload',
  DATASET_READ = 'dataset.read',
  DATASET_UPDATE = 'dataset.update',
  DATASET_DELETE = 'dataset.delete',
  DATASET_EXPORT = 'dataset.export',
  DATASET_SHARE = 'dataset.share',

  // API Management
  API_CREATE_KEYS = 'api.create_keys',
  API_READ_KEYS = 'api.read_keys',
  API_UPDATE_KEYS = 'api.update_keys',
  API_DELETE_KEYS = 'api.delete_keys',
  API_VIEW_USAGE = 'api.view_usage',
  API_MANAGE_WEBHOOKS = 'api.manage_webhooks',

  // Analytics & Reporting
  ANALYTICS_VIEW = 'analytics.view',
  ANALYTICS_EXPORT = 'analytics.export',
  ANALYTICS_ADVANCED = 'analytics.advanced',
  REPORTS_CREATE = 'reports.create',
  REPORTS_READ = 'reports.read',
  REPORTS_UPDATE = 'reports.update',
  REPORTS_DELETE = 'reports.delete',
  REPORTS_SCHEDULE = 'reports.schedule',

  // Integration Management
  INTEGRATION_CREATE = 'integration.create',
  INTEGRATION_READ = 'integration.read',
  INTEGRATION_UPDATE = 'integration.update',
  INTEGRATION_DELETE = 'integration.delete',
  INTEGRATION_CONFIGURE = 'integration.configure',

  // Security Management
  SECURITY_MANAGE_SSO = 'security.manage_sso',
  SECURITY_MANAGE_MFA = 'security.manage_mfa',
  SECURITY_MANAGE_IP_WHITELIST = 'security.manage_ip_whitelist',
  SECURITY_VIEW_SECURITY_LOGS = 'security.view_security_logs',
  SECURITY_MANAGE_SESSIONS = 'security.manage_sessions',
}

export interface RolePermissions {
  [OrganizationRole.OWNER]: OrganizationPermission[];
  [OrganizationRole.ADMIN]: OrganizationPermission[];
  [OrganizationRole.DEVELOPER]: OrganizationPermission[];
  [OrganizationRole.VIEWER]: OrganizationPermission[];
}

export const ORGANIZATION_ROLE_PERMISSIONS: RolePermissions = {
  [OrganizationRole.OWNER]: [
    // Full organization control
    OrganizationPermission.ORG_READ,
    OrganizationPermission.ORG_UPDATE,
    OrganizationPermission.ORG_DELETE,
    OrganizationPermission.ORG_MANAGE_SETTINGS,
    OrganizationPermission.ORG_MANAGE_BILLING,
    OrganizationPermission.ORG_MANAGE_SUBSCRIPTION,
    OrganizationPermission.ORG_VIEW_AUDIT_LOGS,
    OrganizationPermission.ORG_MANAGE_INTEGRATIONS,
    OrganizationPermission.ORG_MANAGE_BRANDING,

    // Full member management
    OrganizationPermission.MEMBER_INVITE,
    OrganizationPermission.MEMBER_READ,
    OrganizationPermission.MEMBER_UPDATE,
    OrganizationPermission.MEMBER_REMOVE,
    OrganizationPermission.MEMBER_MANAGE_ROLES,
    OrganizationPermission.MEMBER_DEACTIVATE,

    // Full project management
    OrganizationPermission.PROJECT_CREATE,
    OrganizationPermission.PROJECT_READ,
    OrganizationPermission.PROJECT_UPDATE,
    OrganizationPermission.PROJECT_DELETE,
    OrganizationPermission.PROJECT_MANAGE_MEMBERS,
    OrganizationPermission.PROJECT_ARCHIVE,
    OrganizationPermission.PROJECT_EXPORT,

    // Full benchmark management
    OrganizationPermission.BENCHMARK_CREATE,
    OrganizationPermission.BENCHMARK_READ,
    OrganizationPermission.BENCHMARK_UPDATE,
    OrganizationPermission.BENCHMARK_DELETE,
    OrganizationPermission.BENCHMARK_RUN,
    OrganizationPermission.BENCHMARK_VIEW_RESULTS,
    OrganizationPermission.BENCHMARK_EXPORT,
    OrganizationPermission.BENCHMARK_SHARE,

    // Full dataset management
    OrganizationPermission.DATASET_UPLOAD,
    OrganizationPermission.DATASET_READ,
    OrganizationPermission.DATASET_UPDATE,
    OrganizationPermission.DATASET_DELETE,
    OrganizationPermission.DATASET_EXPORT,
    OrganizationPermission.DATASET_SHARE,

    // Full API management
    OrganizationPermission.API_CREATE_KEYS,
    OrganizationPermission.API_READ_KEYS,
    OrganizationPermission.API_UPDATE_KEYS,
    OrganizationPermission.API_DELETE_KEYS,
    OrganizationPermission.API_VIEW_USAGE,
    OrganizationPermission.API_MANAGE_WEBHOOKS,

    // Full analytics and reporting
    OrganizationPermission.ANALYTICS_VIEW,
    OrganizationPermission.ANALYTICS_EXPORT,
    OrganizationPermission.ANALYTICS_ADVANCED,
    OrganizationPermission.REPORTS_CREATE,
    OrganizationPermission.REPORTS_READ,
    OrganizationPermission.REPORTS_UPDATE,
    OrganizationPermission.REPORTS_DELETE,
    OrganizationPermission.REPORTS_SCHEDULE,

    // Full integration management
    OrganizationPermission.INTEGRATION_CREATE,
    OrganizationPermission.INTEGRATION_READ,
    OrganizationPermission.INTEGRATION_UPDATE,
    OrganizationPermission.INTEGRATION_DELETE,
    OrganizationPermission.INTEGRATION_CONFIGURE,

    // Full security management
    OrganizationPermission.SECURITY_MANAGE_SSO,
    OrganizationPermission.SECURITY_MANAGE_MFA,
    OrganizationPermission.SECURITY_MANAGE_IP_WHITELIST,
    OrganizationPermission.SECURITY_VIEW_SECURITY_LOGS,
    OrganizationPermission.SECURITY_MANAGE_SESSIONS,
  ],

  [OrganizationRole.ADMIN]: [
    // Organization management (except delete)
    OrganizationPermission.ORG_READ,
    OrganizationPermission.ORG_UPDATE,
    OrganizationPermission.ORG_MANAGE_SETTINGS,
    OrganizationPermission.ORG_VIEW_AUDIT_LOGS,
    OrganizationPermission.ORG_MANAGE_INTEGRATIONS,
    OrganizationPermission.ORG_MANAGE_BRANDING,

    // Member management (except removing owners)
    OrganizationPermission.MEMBER_INVITE,
    OrganizationPermission.MEMBER_READ,
    OrganizationPermission.MEMBER_UPDATE,
    OrganizationPermission.MEMBER_REMOVE,
    OrganizationPermission.MEMBER_MANAGE_ROLES,
    OrganizationPermission.MEMBER_DEACTIVATE,

    // Full project management
    OrganizationPermission.PROJECT_CREATE,
    OrganizationPermission.PROJECT_READ,
    OrganizationPermission.PROJECT_UPDATE,
    OrganizationPermission.PROJECT_DELETE,
    OrganizationPermission.PROJECT_MANAGE_MEMBERS,
    OrganizationPermission.PROJECT_ARCHIVE,
    OrganizationPermission.PROJECT_EXPORT,

    // Full benchmark management
    OrganizationPermission.BENCHMARK_CREATE,
    OrganizationPermission.BENCHMARK_READ,
    OrganizationPermission.BENCHMARK_UPDATE,
    OrganizationPermission.BENCHMARK_DELETE,
    OrganizationPermission.BENCHMARK_RUN,
    OrganizationPermission.BENCHMARK_VIEW_RESULTS,
    OrganizationPermission.BENCHMARK_EXPORT,
    OrganizationPermission.BENCHMARK_SHARE,

    // Full dataset management
    OrganizationPermission.DATASET_UPLOAD,
    OrganizationPermission.DATASET_READ,
    OrganizationPermission.DATASET_UPDATE,
    OrganizationPermission.DATASET_DELETE,
    OrganizationPermission.DATASET_EXPORT,
    OrganizationPermission.DATASET_SHARE,

    // Full API management
    OrganizationPermission.API_CREATE_KEYS,
    OrganizationPermission.API_READ_KEYS,
    OrganizationPermission.API_UPDATE_KEYS,
    OrganizationPermission.API_DELETE_KEYS,
    OrganizationPermission.API_VIEW_USAGE,
    OrganizationPermission.API_MANAGE_WEBHOOKS,

    // Analytics and reporting
    OrganizationPermission.ANALYTICS_VIEW,
    OrganizationPermission.ANALYTICS_EXPORT,
    OrganizationPermission.ANALYTICS_ADVANCED,
    OrganizationPermission.REPORTS_CREATE,
    OrganizationPermission.REPORTS_READ,
    OrganizationPermission.REPORTS_UPDATE,
    OrganizationPermission.REPORTS_DELETE,
    OrganizationPermission.REPORTS_SCHEDULE,

    // Integration management
    OrganizationPermission.INTEGRATION_CREATE,
    OrganizationPermission.INTEGRATION_READ,
    OrganizationPermission.INTEGRATION_UPDATE,
    OrganizationPermission.INTEGRATION_DELETE,
    OrganizationPermission.INTEGRATION_CONFIGURE,

    // Security management (except billing)
    OrganizationPermission.SECURITY_MANAGE_SSO,
    OrganizationPermission.SECURITY_MANAGE_MFA,
    OrganizationPermission.SECURITY_MANAGE_IP_WHITELIST,
    OrganizationPermission.SECURITY_VIEW_SECURITY_LOGS,
    OrganizationPermission.SECURITY_MANAGE_SESSIONS,
  ],

  [OrganizationRole.DEVELOPER]: [
    // Basic organization access
    OrganizationPermission.ORG_READ,

    // Member management (read only)
    OrganizationPermission.MEMBER_READ,

    // Project management (create, read, update own)
    OrganizationPermission.PROJECT_CREATE,
    OrganizationPermission.PROJECT_READ,
    OrganizationPermission.PROJECT_UPDATE,
    OrganizationPermission.PROJECT_ARCHIVE,
    OrganizationPermission.PROJECT_EXPORT,

    // Benchmark management
    OrganizationPermission.BENCHMARK_CREATE,
    OrganizationPermission.BENCHMARK_READ,
    OrganizationPermission.BENCHMARK_UPDATE,
    OrganizationPermission.BENCHMARK_DELETE,
    OrganizationPermission.BENCHMARK_RUN,
    OrganizationPermission.BENCHMARK_VIEW_RESULTS,
    OrganizationPermission.BENCHMARK_EXPORT,
    OrganizationPermission.BENCHMARK_SHARE,

    // Dataset management
    OrganizationPermission.DATASET_UPLOAD,
    OrganizationPermission.DATASET_READ,
    OrganizationPermission.DATASET_UPDATE,
    OrganizationPermission.DATASET_DELETE,
    OrganizationPermission.DATASET_EXPORT,
    OrganizationPermission.DATASET_SHARE,

    // API management
    OrganizationPermission.API_CREATE_KEYS,
    OrganizationPermission.API_READ_KEYS,
    OrganizationPermission.API_UPDATE_KEYS,
    OrganizationPermission.API_DELETE_KEYS,
    OrganizationPermission.API_VIEW_USAGE,

    // Basic analytics
    OrganizationPermission.ANALYTICS_VIEW,
    OrganizationPermission.ANALYTICS_EXPORT,

    // Integration management (read only)
    OrganizationPermission.INTEGRATION_READ,
  ],

  [OrganizationRole.VIEWER]: [
    // Read-only access
    OrganizationPermission.ORG_READ,
    OrganizationPermission.MEMBER_READ,
    OrganizationPermission.PROJECT_READ,
    OrganizationPermission.BENCHMARK_READ,
    OrganizationPermission.BENCHMARK_VIEW_RESULTS,
    OrganizationPermission.BENCHMARK_EXPORT,
    OrganizationPermission.DATASET_READ,
    OrganizationPermission.DATASET_EXPORT,
    OrganizationPermission.API_READ_KEYS,
    OrganizationPermission.API_VIEW_USAGE,
    OrganizationPermission.ANALYTICS_VIEW,
    OrganizationPermission.ANALYTICS_EXPORT,
    OrganizationPermission.REPORTS_READ,
    OrganizationPermission.INTEGRATION_READ,
  ],
};

export interface RoleHierarchy {
  [key: string]: OrganizationRole[];
}

export const ROLE_HIERARCHY: RoleHierarchy = {
  [OrganizationRole.OWNER]: [OrganizationRole.OWNER],
  [OrganizationRole.ADMIN]: [
    OrganizationRole.ADMIN,
    OrganizationRole.DEVELOPER,
    OrganizationRole.VIEWER,
  ],
  [OrganizationRole.DEVELOPER]: [OrganizationRole.DEVELOPER, OrganizationRole.VIEWER],
  [OrganizationRole.VIEWER]: [OrganizationRole.VIEWER],
};

export interface PermissionCategory {
  name: string;
  permissions: Array<{
    key: OrganizationPermission;
    name: string;
    description: string;
  }>;
}

export const PERMISSION_CATEGORIES: PermissionCategory[] = [
  {
    name: 'Organization Management',
    permissions: [
      {
        key: OrganizationPermission.ORG_READ,
        name: 'View Organization',
        description: 'View organization details and settings',
      },
      {
        key: OrganizationPermission.ORG_UPDATE,
        name: 'Update Organization',
        description: 'Update organization information and settings',
      },
      {
        key: OrganizationPermission.ORG_DELETE,
        name: 'Delete Organization',
        description: 'Delete the entire organization',
      },
      {
        key: OrganizationPermission.ORG_MANAGE_SETTINGS,
        name: 'Manage Settings',
        description: 'Manage organization settings and preferences',
      },
      {
        key: OrganizationPermission.ORG_MANAGE_BILLING,
        name: 'Manage Billing',
        description: 'Manage billing information and payments',
      },
      {
        key: OrganizationPermission.ORG_VIEW_AUDIT_LOGS,
        name: 'View Audit Logs',
        description: 'View organization audit logs and activity',
      },
    ],
  },
  {
    name: 'Member Management',
    permissions: [
      {
        key: OrganizationPermission.MEMBER_INVITE,
        name: 'Invite Members',
        description: 'Invite new members to the organization',
      },
      {
        key: OrganizationPermission.MEMBER_READ,
        name: 'View Members',
        description: 'View organization member list and details',
      },
      {
        key: OrganizationPermission.MEMBER_UPDATE,
        name: 'Update Members',
        description: 'Update member information and roles',
      },
      {
        key: OrganizationPermission.MEMBER_REMOVE,
        name: 'Remove Members',
        description: 'Remove members from the organization',
      },
      {
        key: OrganizationPermission.MEMBER_MANAGE_ROLES,
        name: 'Manage Roles',
        description: 'Assign and manage member roles',
      },
    ],
  },
  {
    name: 'Project Management',
    permissions: [
      {
        key: OrganizationPermission.PROJECT_CREATE,
        name: 'Create Projects',
        description: 'Create new projects within the organization',
      },
      {
        key: OrganizationPermission.PROJECT_READ,
        name: 'View Projects',
        description: 'View projects and their details',
      },
      {
        key: OrganizationPermission.PROJECT_UPDATE,
        name: 'Update Projects',
        description: 'Update project information and settings',
      },
      {
        key: OrganizationPermission.PROJECT_DELETE,
        name: 'Delete Projects',
        description: 'Delete projects from the organization',
      },
    ],
  },
  {
    name: 'Benchmark Management',
    permissions: [
      {
        key: OrganizationPermission.BENCHMARK_CREATE,
        name: 'Create Benchmarks',
        description: 'Create new benchmarks',
      },
      {
        key: OrganizationPermission.BENCHMARK_READ,
        name: 'View Benchmarks',
        description: 'View benchmarks and their details',
      },
      {
        key: OrganizationPermission.BENCHMARK_RUN,
        name: 'Run Benchmarks',
        description: 'Execute benchmark runs',
      },
      {
        key: OrganizationPermission.BENCHMARK_VIEW_RESULTS,
        name: 'View Results',
        description: 'View benchmark results and analytics',
      },
    ],
  },
  {
    name: 'API Management',
    permissions: [
      {
        key: OrganizationPermission.API_CREATE_KEYS,
        name: 'Create API Keys',
        description: 'Create new API keys for programmatic access',
      },
      {
        key: OrganizationPermission.API_READ_KEYS,
        name: 'View API Keys',
        description: 'View existing API keys and their usage',
      },
      {
        key: OrganizationPermission.API_VIEW_USAGE,
        name: 'View API Usage',
        description: 'View API usage statistics and analytics',
      },
    ],
  },
  {
    name: 'Analytics & Reporting',
    permissions: [
      {
        key: OrganizationPermission.ANALYTICS_VIEW,
        name: 'View Analytics',
        description: 'View organization analytics and metrics',
      },
      {
        key: OrganizationPermission.ANALYTICS_EXPORT,
        name: 'Export Analytics',
        description: 'Export analytics data and reports',
      },
      {
        key: OrganizationPermission.ANALYTICS_ADVANCED,
        name: 'Advanced Analytics',
        description: 'Access advanced analytics features',
      },
    ],
  },
];
```

### Subtask 3.2: Create role assignment and management endpoints

**Objective**: Build endpoints for role management and assignment

**File**: `src/controllers/role-controller.ts`

```typescript
import { Request, Response } from 'express';
import { body, param, validationResult } from 'express-validator';
import { RoleService } from '../services/role-service';
import { OrganizationRole, OrganizationPermission } from '../models/organization-roles';
import { logger } from '../observability/logger';
import { ApiError } from '../utils/api-error';

export class RoleController {
  constructor(private roleService: RoleService) {}

  // Validation middleware
  static assignRoleValidation = [
    param('organizationId').isUUID().withMessage('Valid organization ID is required'),
    param('userId').isUUID().withMessage('Valid user ID is required'),
    body('role').isIn(Object.values(OrganizationRole)).withMessage('Valid role is required'),
    body('permissions').optional().isArray().withMessage('Permissions must be an array'),
    body('permissions.*')
      .optional()
      .isIn(Object.values(OrganizationPermission))
      .withMessage('Each permission must be valid'),
  ];

  static updateRoleValidation = [
    param('organizationId').isUUID().withMessage('Valid organization ID is required'),
    param('userId').isUUID().withMessage('Valid user ID is required'),
    body('role')
      .optional()
      .isIn(Object.values(OrganizationRole))
      .withMessage('Valid role is required'),
    body('permissions').optional().isArray().withMessage('Permissions must be an array'),
    body('permissions.*')
      .optional()
      .isIn(Object.values(OrganizationPermission))
      .withMessage('Each permission must be valid'),
  ];

  async assignRole(req: Request, res: Response): Promise<void> {
    try {
      // Check validation errors
      const errors = validationResult(req);
      if (!errors.isEmpty()) {
        throw new ApiError(400, 'Validation failed', errors.array());
      }

      const { organizationId, userId } = req.params;
      const { role, permissions } = req.body;
      const currentUserId = req.user!.sub;
      const currentUserRole = req.user!.role;

      // Check if current user can assign roles
      await this.roleService.validateRoleAssignment(
        currentUserId,
        currentUserRole!,
        userId,
        role as OrganizationRole,
        organizationId
      );

      // Assign role
      const membership = await this.roleService.assignRole(
        userId,
        organizationId,
        role as OrganizationRole,
        permissions,
        currentUserId
      );

      // Emit event for audit trail
      await this.roleService.emitEvent('ROLE.ASSIGNED', {
        organizationId,
        userId,
        role,
        permissions,
        assignedBy: currentUserId,
        ipAddress: req.ip,
        userAgent: req.get('User-Agent'),
      });

      logger.info('Role assigned', {
        organizationId,
        userId,
        role,
        assignedBy: currentUserId,
      });

      res.status(201).json({
        message: 'Role assigned successfully',
        membership: {
          id: membership.id,
          userId: membership.user_id,
          organizationId: membership.organization_id,
          role: membership.role,
          permissions: membership.permissions,
          status: membership.status,
          joinedAt: membership.joined_at,
        },
      });
    } catch (error) {
      if (error instanceof ApiError) {
        res.status(error.statusCode).json({
          error: error.message,
          details: error.details,
        });
      } else {
        logger.error('Assign role error', { error });
        res.status(500).json({ error: 'Internal server error' });
      }
    }
  }

  async updateRole(req: Request, res: Response): Promise<void> {
    try {
      // Check validation errors
      const errors = validationResult(req);
      if (!errors.isEmpty()) {
        throw new ApiError(400, 'Validation failed', errors.array());
      }

      const { organizationId, userId } = req.params;
      const { role, permissions } = req.body;
      const currentUserId = req.user!.sub;
      const currentUserRole = req.user!.role;

      // Check if current user can update roles
      await this.roleService.validateRoleUpdate(
        currentUserId,
        currentUserRole!,
        userId,
        role as OrganizationRole,
        organizationId
      );

      // Update role
      const membership = await this.roleService.updateRole(
        userId,
        organizationId,
        role as OrganizationRole,
        permissions,
        currentUserId
      );

      // Emit event for audit trail
      await this.roleService.emitEvent('ROLE.UPDATED', {
        organizationId,
        userId,
        role,
        permissions,
        updatedBy: currentUserId,
        ipAddress: req.ip,
        userAgent: req.get('User-Agent'),
      });

      logger.info('Role updated', {
        organizationId,
        userId,
        role,
        updatedBy: currentUserId,
      });

      res.json({
        message: 'Role updated successfully',
        membership: {
          id: membership.id,
          userId: membership.user_id,
          organizationId: membership.organization_id,
          role: membership.role,
          permissions: membership.permissions,
          status: membership.status,
          updatedAt: membership.updated_at,
        },
      });
    } catch (error) {
      if (error instanceof ApiError) {
        res.status(error.statusCode).json({
          error: error.message,
          details: error.details,
        });
      } else {
        logger.error('Update role error', { error });
        res.status(500).json({ error: 'Internal server error' });
      }
    }
  }

  async removeRole(req: Request, res: Response): Promise<void> {
    try {
      const { organizationId, userId } = req.params;
      const currentUserId = req.user!.sub;
      const currentUserRole = req.user!.role;

      // Check if current user can remove roles
      await this.roleService.validateRoleRemoval(
        currentUserId,
        currentUserRole!,
        userId,
        organizationId
      );

      // Remove role (set membership to inactive)
      await this.roleService.removeRole(userId, organizationId, currentUserId);

      // Emit event for audit trail
      await this.roleService.emitEvent('ROLE.REMOVED', {
        organizationId,
        userId,
        removedBy: currentUserId,
        ipAddress: req.ip,
        userAgent: req.get('User-Agent'),
      });

      logger.info('Role removed', {
        organizationId,
        userId,
        removedBy: currentUserId,
      });

      res.json({
        message: 'Role removed successfully',
      });
    } catch (error) {
      if (error instanceof ApiError) {
        res.status(error.statusCode).json({
          error: error.message,
        });
      } else {
        logger.error('Remove role error', { error });
        res.status(500).json({ error: 'Internal server error' });
      }
    }
  }

  async getOrganizationRoles(req: Request, res: Response): Promise<void> {
    try {
      const { organizationId } = req.params;

      const roles = await this.roleService.getOrganizationRoles(organizationId);

      res.json({
        organizationId,
        roles: roles.map((role) => ({
          id: role.id,
          userId: role.user_id,
          email: role.email,
          firstName: role.first_name,
          lastName: role.last_name,
          role: role.role,
          permissions: role.permissions,
          status: role.status,
          joinedAt: role.joined_at,
          lastActiveAt: role.last_active_at,
        })),
      });
    } catch (error) {
      logger.error('Get organization roles error', { error });
      res.status(500).json({ error: 'Internal server error' });
    }
  }

  async getRolePermissions(req: Request, res: Response): Promise<void> {
    try {
      const { organizationId } = req.params;
      const { role } = req.query;

      if (!role || typeof role !== 'string') {
        throw new ApiError(400, 'Role parameter is required');
      }

      const permissions = await this.roleService.getRolePermissions(
        role as OrganizationRole,
        organizationId
      );

      res.json({
        role,
        organizationId,
        permissions,
      });
    } catch (error) {
      if (error instanceof ApiError) {
        res.status(error.statusCode).json({
          error: error.message,
        });
      } else {
        logger.error('Get role permissions error', { error });
        res.status(500).json({ error: 'Internal server error' });
      }
    }
  }

  async getAvailableRoles(req: Request, res: Response): Promise<void> {
    try {
      const { organizationId } = req.params;
      const currentUserId = req.user!.sub;
      const currentUserRole = req.user!.role;

      const availableRoles = await this.roleService.getAvailableRoles(
        currentUserId,
        currentUserRole!,
        organizationId
      );

      res.json({
        organizationId,
        availableRoles,
      });
    } catch (error) {
      logger.error('Get available roles error', { error });
      res.status(500).json({ error: 'Internal server error' });
    }
  }

  async getPermissionCategories(req: Request, res: Response): Promise<void> {
    try {
      const categories = await this.roleService.getPermissionCategories();

      res.json({
        categories,
      });
    } catch (error) {
      logger.error('Get permission categories error', { error });
      res.status(500).json({ error: 'Internal server error' });
    }
  }
}
```

### Subtask 3.3: Implement permission checking middleware

**Objective**: Create middleware for role-based permission checking

**File**: `src/middleware/role-permission-middleware.ts`

```typescript
import { Request, Response, NextFunction } from 'express';
import { RoleService } from '../services/role-service';
import { OrganizationRole, OrganizationPermission } from '../models/organization-roles';
import { logger } from '../observability/logger';
import { ApiError } from '../utils/api-error';

export interface RolePermissionOptions {
  organizationId?: string;
  requireOwnership?: boolean;
  allowSelf?: boolean;
}

export class RolePermissionMiddleware {
  constructor(private roleService: RoleService) {}

  // Middleware to check if user has specific permission
  requirePermission(permission: OrganizationPermission, options: RolePermissionOptions = {}) {
    return async (req: Request, res: Response, next: NextFunction): Promise<void> => {
      try {
        const userId = req.user?.sub;
        const userRole = req.user?.role as OrganizationRole;
        const organizationId = options.organizationId || req.user?.organizationId;

        if (!userId || !userRole || !organizationId) {
          throw new ApiError(401, 'Authentication required');
        }

        const hasPermission = await this.roleService.hasPermission(
          userId,
          userRole,
          organizationId,
          permission,
          options
        );

        if (!hasPermission) {
          throw new ApiError(403, 'Insufficient permissions', {
            requiredPermission: permission,
            userRole,
            organizationId,
          });
        }

        next();
      } catch (error) {
        if (error instanceof ApiError) {
          res.status(error.statusCode).json({
            error: error.message,
            details: error.details,
          });
        } else {
          logger.error('Permission check failed', { error });
          res.status(500).json({ error: 'Permission check failed' });
        }
      }
    };
  }

  // Middleware to check if user has any of the specified permissions
  requireAnyPermission(permissions: OrganizationPermission[], options: RolePermissionOptions = {}) {
    return async (req: Request, res: Response, next: NextFunction): Promise<void> => {
      try {
        const userId = req.user?.sub;
        const userRole = req.user?.role as OrganizationRole;
        const organizationId = options.organizationId || req.user?.organizationId;

        if (!userId || !userRole || !organizationId) {
          throw new ApiError(401, 'Authentication required');
        }

        const hasPermission = await this.roleService.hasAnyPermission(
          userId,
          userRole,
          organizationId,
          permissions,
          options
        );

        if (!hasPermission) {
          throw new ApiError(403, 'Insufficient permissions', {
            requiredPermissions: permissions,
            userRole,
            organizationId,
          });
        }

        next();
      } catch (error) {
        if (error instanceof ApiError) {
          res.status(error.statusCode).json({
            error: error.message,
            details: error.details,
          });
        } else {
          logger.error('Permission check failed', { error });
          res.status(500).json({ error: 'Permission check failed' });
        }
      }
    };
  }

  // Middleware to check if user has all specified permissions
  requireAllPermissions(
    permissions: OrganizationPermission[],
    options: RolePermissionOptions = {}
  ) {
    return async (req: Request, res: Response, next: NextFunction): Promise<void> => {
      try {
        const userId = req.user?.sub;
        const userRole = req.user?.role as OrganizationRole;
        const organizationId = options.organizationId || req.user?.organizationId;

        if (!userId || !userRole || !organizationId) {
          throw new ApiError(401, 'Authentication required');
        }

        const hasPermission = await this.roleService.hasAllPermissions(
          userId,
          userRole,
          organizationId,
          permissions,
          options
        );

        if (!hasPermission) {
          throw new ApiError(403, 'Insufficient permissions', {
            requiredPermissions: permissions,
            userRole,
            organizationId,
          });
        }

        next();
      } catch (error) {
        if (error instanceof ApiError) {
          res.status(error.statusCode).json({
            error: error.message,
            details: error.details,
          });
        } else {
          logger.error('Permission check failed', { error });
          res.status(500).json({ error: 'Permission check failed' });
        }
      }
    };
  }

  // Middleware to check if user has specific role or higher
  requireRole(minRole: OrganizationRole) {
    return async (req: Request, res: Response, next: NextFunction): Promise<void> => {
      try {
        const userId = req.user?.sub;
        const userRole = req.user?.role as OrganizationRole;
        const organizationId = req.user?.organizationId;

        if (!userId || !userRole || !organizationId) {
          throw new ApiError(401, 'Authentication required');
        }

        const hasRequiredRole = await this.roleService.hasRoleOrHigher(
          userId,
          userRole,
          organizationId,
          minRole
        );

        if (!hasRequiredRole) {
          throw new ApiError(403, 'Insufficient role level', {
            requiredRole: minRole,
            userRole,
            organizationId,
          });
        }

        next();
      } catch (error) {
        if (error instanceof ApiError) {
          res.status(error.statusCode).json({
            error: error.message,
            details: error.details,
          });
        } else {
          logger.error('Role check failed', { error });
          res.status(500).json({ error: 'Role check failed' });
        }
      }
    };
  }

  // Middleware to check resource ownership or admin role
  requireOwnershipOrAdmin(
    getResourceOwnerId: (req: Request) => Promise<string | undefined>,
    options: RolePermissionOptions = {}
  ) {
    return async (req: Request, res: Response, next: NextFunction): Promise<void> => {
      try {
        const userId = req.user?.sub;
        const userRole = req.user?.role as OrganizationRole;
        const organizationId = options.organizationId || req.user?.organizationId;

        if (!userId || !userRole || !organizationId) {
          throw new ApiError(401, 'Authentication required');
        }

        const resourceOwnerId = await getResourceOwnerId(req);
        const isOwner = resourceOwnerId === userId;

        // Check if user is owner or has admin privileges
        const isAdmin = await this.roleService.hasPermission(
          userId,
          userRole,
          organizationId,
          OrganizationPermission.MEMBER_MANAGE_ROLES,
          options
        );

        if (!isOwner && !isAdmin) {
          throw new ApiError(403, 'Access denied: must be resource owner or admin', {
            isOwner,
            isAdmin,
            userRole,
            organizationId,
          });
        }

        next();
      } catch (error) {
        if (error instanceof ApiError) {
          res.status(error.statusCode).json({
            error: error.message,
            details: error.details,
          });
        } else {
          logger.error('Ownership check failed', { error });
          res.status(500).json({ error: 'Ownership check failed' });
        }
      }
    };
  }

  // Middleware to check if user can manage other users
  requireUserManagement() {
    return this.requirePermission(OrganizationPermission.MEMBER_MANAGE_ROLES);
  }

  // Middleware to check if user can manage organization settings
  requireOrganizationManagement() {
    return this.requirePermission(OrganizationPermission.ORG_MANAGE_SETTINGS);
  }

  // Middleware to check if user can manage billing
  requireBillingManagement() {
    return this.requirePermission(OrganizationPermission.ORG_MANAGE_BILLING);
  }

  // Middleware to check if user can create projects
  requireProjectCreation() {
    return this.requirePermission(OrganizationPermission.PROJECT_CREATE);
  }

  // Middleware to check if user can manage benchmarks
  requireBenchmarkManagement() {
    return this.requireAnyPermission([
      OrganizationPermission.BENCHMARK_CREATE,
      OrganizationPermission.BENCHMARK_UPDATE,
      OrganizationPermission.BENCHMARK_DELETE,
    ]);
  }

  // Middleware to check if user can view analytics
  requireAnalyticsView() {
    return this.requirePermission(OrganizationPermission.ANALYTICS_VIEW);
  }

  // Middleware to check if user can manage API keys
  requireApiKeyManagement() {
    return this.requirePermission(OrganizationPermission.API_CREATE_KEYS);
  }

  // Middleware to load user permissions into request
  loadPermissions() {
    return async (req: Request, res: Response, next: NextFunction): Promise<void> => {
      try {
        const userId = req.user?.sub;
        const userRole = req.user?.role as OrganizationRole;
        const organizationId = req.user?.organizationId;

        if (!userId || !userRole || !organizationId) {
          return next();
        }

        const permissions = await this.roleService.getUserPermissions(
          userId,
          userRole,
          organizationId
        );

        // Add permissions to request object
        req.user.permissions = permissions;

        next();
      } catch (error) {
        logger.error('Failed to load user permissions', { error });
        next(); // Don't fail request if permission loading fails
      }
    };
  }
}

// Extend Express Request interface
declare global {
  namespace Express {
    interface Request {
      user?: {
        sub: string;
        email: string;
        organizationId?: string;
        role?: OrganizationRole;
        permissions?: OrganizationPermission[];
      };
    }
  }
}
```

### Subtask 3.4: Add role hierarchy and inheritance

**Objective**: Implement role hierarchy with permission inheritance

**File**: `src/services/role-service.ts`

```typescript
import { db } from '../database/connection';
import {
  OrganizationRole,
  OrganizationPermission,
  ORGANIZATION_ROLE_PERMISSIONS,
  ROLE_HIERARCHY,
} from '../models/organization-roles';
import { logger } from '../observability/logger';
import { ApiError } from '../utils/api-error';

export interface RoleAssignment {
  id: string;
  userId: string;
  organizationId: string;
  role: OrganizationRole;
  permissions?: OrganizationPermission[];
  status: 'active' | 'inactive' | 'invited';
  joinedAt?: Date;
  lastActiveAt?: Date;
}

export class RoleService {
  // Check if user has specific permission
  async hasPermission(
    userId: string,
    userRole: OrganizationRole,
    organizationId: string,
    permission: OrganizationPermission,
    options: {
      requireOwnership?: boolean;
      allowSelf?: boolean;
    } = {}
  ): Promise<boolean> {
    try {
      // Get user's effective permissions (including role hierarchy)
      const effectivePermissions = await this.getEffectivePermissions(
        userId,
        userRole,
        organizationId
      );

      // Check if permission is granted
      if (!effectivePermissions.includes(permission)) {
        return false;
      }

      // Additional checks for ownership or self-access
      if (options.requireOwnership) {
        return await this.checkOwnership(userId, organizationId);
      }

      if (options.allowSelf) {
        return true; // Self-access is allowed if permission is granted
      }

      return true;
    } catch (error) {
      logger.error('Permission check failed', { error, userId, permission });
      return false;
    }
  }

  // Check if user has any of the specified permissions
  async hasAnyPermission(
    userId: string,
    userRole: OrganizationRole,
    organizationId: string,
    permissions: OrganizationPermission[],
    options: {
      requireOwnership?: boolean;
      allowSelf?: boolean;
    } = {}
  ): Promise<boolean> {
    for (const permission of permissions) {
      if (await this.hasPermission(userId, userRole, organizationId, permission, options)) {
        return true;
      }
    }
    return false;
  }

  // Check if user has all specified permissions
  async hasAllPermissions(
    userId: string,
    userRole: OrganizationRole,
    organizationId: string,
    permissions: OrganizationPermission[],
    options: {
      requireOwnership?: boolean;
      allowSelf?: boolean;
    } = {}
  ): Promise<boolean> {
    for (const permission of permissions) {
      if (!(await this.hasPermission(userId, userRole, organizationId, permission, options))) {
        return false;
      }
    }
    return true;
  }

  // Check if user has specific role or higher in hierarchy
  async hasRoleOrHigher(
    userId: string,
    userRole: OrganizationRole,
    organizationId: string,
    minRole: OrganizationRole
  ): Promise<boolean> {
    try {
      // Get user's current role in organization
      const membership = await this.getUserMembership(userId, organizationId);
      if (!membership) {
        return false;
      }

      // Check role hierarchy
      const userHierarchyLevel = this.getRoleHierarchyLevel(userRole);
      const requiredHierarchyLevel = this.getRoleHierarchyLevel(minRole);

      return userHierarchyLevel <= requiredHierarchyLevel;
    } catch (error) {
      logger.error('Role hierarchy check failed', { error, userId, userRole, minRole });
      return false;
    }
  }

  // Get effective permissions for a user (including role hierarchy)
  async getEffectivePermissions(
    userId: string,
    userRole: OrganizationRole,
    organizationId: string
  ): Promise<OrganizationPermission[]> {
    try {
      // Get base permissions for user's role
      const basePermissions = ORGANIZATION_ROLE_PERMISSIONS[userRole] || [];

      // Get additional permissions from user's membership record
      const membership = await this.getUserMembership(userId, organizationId);
      const additionalPermissions = membership?.permissions || [];

      // Combine and deduplicate permissions
      const allPermissions = [...new Set([...basePermissions, ...additionalPermissions])];

      return allPermissions;
    } catch (error) {
      logger.error('Failed to get effective permissions', { error, userId, userRole });
      return [];
    }
  }

  // Assign role to user
  async assignRole(
    userId: string,
    organizationId: string,
    role: OrganizationRole,
    permissions?: OrganizationPermission[],
    assignedBy?: string
  ): Promise<RoleAssignment> {
    try {
      // Check if user is already a member
      const existingMembership = await this.getUserMembership(userId, organizationId);

      if (existingMembership) {
        // Update existing membership
        await db('user_organizations')
          .where('user_id', userId)
          .where('organization_id', organizationId)
          .update({
            role,
            permissions: permissions ? JSON.stringify(permissions) : null,
            status: 'active',
            updated_at: new Date(),
            updated_by: assignedBy,
          });
      } else {
        // Create new membership
        await db('user_organizations').insert({
          user_id: userId,
          organization_id: organizationId,
          role,
          permissions: permissions ? JSON.stringify(permissions) : null,
          status: 'active',
          joined_at: new Date(),
          created_at: new Date(),
          created_by: assignedBy,
        });
      }

      // Get updated membership
      const membership = await this.getUserMembership(userId, organizationId);

      if (!membership) {
        throw new ApiError(500, 'Failed to create role assignment');
      }

      logger.info('Role assigned', {
        userId,
        organizationId,
        role,
        permissions,
        assignedBy,
      });

      return membership;
    } catch (error) {
      logger.error('Failed to assign role', { error, userId, organizationId, role });
      throw new ApiError(500, 'Failed to assign role');
    }
  }

  // Update user's role
  async updateRole(
    userId: string,
    organizationId: string,
    role: OrganizationRole,
    permissions?: OrganizationPermission[],
    updatedBy?: string
  ): Promise<RoleAssignment> {
    try {
      const updated = await db('user_organizations')
        .where('user_id', userId)
        .where('organization_id', organizationId)
        .update({
          role,
          permissions: permissions ? JSON.stringify(permissions) : null,
          updated_at: new Date(),
          updated_by: updatedBy,
        });

      if (updated === 0) {
        throw new ApiError(404, 'User membership not found');
      }

      const membership = await this.getUserMembership(userId, organizationId);

      if (!membership) {
        throw new ApiError(500, 'Failed to update role');
      }

      logger.info('Role updated', {
        userId,
        organizationId,
        role,
        permissions,
        updatedBy,
      });

      return membership;
    } catch (error) {
      if (error instanceof ApiError) {
        throw error;
      }
      logger.error('Failed to update role', { error, userId, organizationId, role });
      throw new ApiError(500, 'Failed to update role');
    }
  }

  // Remove user's role (deactivate membership)
  async removeRole(userId: string, organizationId: string, removedBy?: string): Promise<void> {
    try {
      const updated = await db('user_organizations')
        .where('user_id', userId)
        .where('organization_id', organizationId)
        .update({
          status: 'inactive',
          left_at: new Date(),
          updated_at: new Date(),
          updated_by: removedBy,
        });

      if (updated === 0) {
        throw new ApiError(404, 'User membership not found');
      }

      logger.info('Role removed', {
        userId,
        organizationId,
        removedBy,
      });
    } catch (error) {
      if (error instanceof ApiError) {
        throw error;
      }
      logger.error('Failed to remove role', { error, userId, organizationId });
      throw new ApiError(500, 'Failed to remove role');
    }
  }

  // Get all roles in organization
  async getOrganizationRoles(organizationId: string): Promise<RoleAssignment[]> {
    try {
      return await db('user_organizations')
        .join('users', 'user_organizations.user_id', 'users.id')
        .where('user_organizations.organization_id', organizationId)
        .where('user_organizations.status', 'active')
        .select([
          'user_organizations.id',
          'user_organizations.user_id',
          'user_organizations.role',
          'user_organizations.permissions',
          'user_organizations.status',
          'user_organizations.joined_at',
          'user_organizations.last_active_at',
          'users.email',
          'users.first_name',
          'users.last_name',
        ])
        .orderBy('user_organizations.joined_at', 'asc');
    } catch (error) {
      logger.error('Failed to get organization roles', { error, organizationId });
      throw new ApiError(500, 'Failed to get organization roles');
    }
  }

  // Get available roles for current user
  async getAvailableRoles(
    userId: string,
    userRole: OrganizationRole,
    organizationId: string
  ): Promise<OrganizationRole[]> {
    try {
      // Get user's effective permissions
      const effectivePermissions = await this.getEffectivePermissions(
        userId,
        userRole,
        organizationId
      );

      // Check if user can manage roles
      const canManageRoles = effectivePermissions.includes(
        OrganizationPermission.MEMBER_MANAGE_ROLES
      );

      if (!canManageRoles) {
        // User can only assign roles at or below their level
        const userHierarchyLevel = this.getRoleHierarchyLevel(userRole);
        return Object.values(OrganizationRole).filter((role) => {
          const roleHierarchyLevel = this.getRoleHierarchyLevel(role);
          return roleHierarchyLevel >= userHierarchyLevel;
        });
      }

      // Admins and owners can assign any role
      if (effectivePermissions.includes(OrganizationPermission.ORG_MANAGE_SETTINGS)) {
        return Object.values(OrganizationRole);
      }

      // Default to roles at or below user's level
      const userHierarchyLevel = this.getRoleHierarchyLevel(userRole);
      return Object.values(OrganizationRole).filter((role) => {
        const roleHierarchyLevel = this.getRoleHierarchyLevel(role);
        return roleHierarchyLevel >= userHierarchyLevel;
      });
    } catch (error) {
      logger.error('Failed to get available roles', { error, userId, userRole });
      return [];
    }
  }

  // Validate role assignment
  async validateRoleAssignment(
    currentUserId: string,
    currentUserRole: OrganizationRole,
    targetUserId: string,
    targetRole: OrganizationRole,
    organizationId: string
  ): Promise<void> {
    // Check if current user can manage roles
    const canManageRoles = await this.hasPermission(
      currentUserId,
      currentUserRole,
      organizationId,
      OrganizationPermission.MEMBER_MANAGE_ROLES
    );

    if (!canManageRoles) {
      throw new ApiError(403, 'You do not have permission to manage roles');
    }

    // Check if current user can assign the target role
    const canAssignRole = await this.canAssignRole(
      currentUserId,
      currentUserRole,
      targetRole,
      organizationId
    );

    if (!canAssignRole) {
      throw new ApiError(403, 'You cannot assign this role');
    }

    // Check if target user is the last owner
    if (targetRole !== OrganizationRole.OWNER) {
      const isLastOwner = await this.isLastOwner(targetUserId, organizationId);
      if (isLastOwner) {
        throw new ApiError(400, 'Cannot remove the last owner from the organization');
      }
    }
  }

  // Get permission categories
  async getPermissionCategories() {
    const { PERMISSION_CATEGORIES } = await import('../models/organization-roles');
    return PERMISSION_CATEGORIES;
  }

  private async getUserMembership(
    userId: string,
    organizationId: string
  ): Promise<RoleAssignment | null> {
    const membership = await db('user_organizations')
      .where('user_id', userId)
      .where('organization_id', organizationId)
      .where('status', 'active')
      .first();

    if (!membership) {
      return null;
    }

    return {
      id: membership.id,
      userId: membership.user_id,
      organizationId: membership.organization_id,
      role: membership.role as OrganizationRole,
      permissions: membership.permissions ? JSON.parse(membership.permissions) : undefined,
      status: membership.status as 'active' | 'inactive' | 'invited',
      joinedAt: membership.joined_at,
      lastActiveAt: membership.last_active_at,
    };
  }

  private getRoleHierarchyLevel(role: OrganizationRole): number {
    const hierarchy = {
      [OrganizationRole.OWNER]: 0,
      [OrganizationRole.ADMIN]: 1,
      [OrganizationRole.DEVELOPER]: 2,
      [OrganizationRole.VIEWER]: 3,
    };
    return hierarchy[role] || 999;
  }

  private async canAssignRole(
    userId: string,
    userRole: OrganizationRole,
    targetRole: OrganizationRole,
    organizationId: string
  ): Promise<boolean> {
    // Get user's effective permissions
    const effectivePermissions = await this.getEffectivePermissions(
      userId,
      userRole,
      organizationId
    );

    // Owners can assign any role
    if (effectivePermissions.includes(OrganizationPermission.ORG_DELETE)) {
      return true;
    }

    // Admins can assign any role except owner
    if (effectivePermissions.includes(OrganizationPermission.ORG_MANAGE_SETTINGS)) {
      return targetRole !== OrganizationRole.OWNER;
    }

    // Check role hierarchy
    const userHierarchyLevel = this.getRoleHierarchyLevel(userRole);
    const targetHierarchyLevel = this.getRoleHierarchyLevel(targetRole);

    return targetHierarchyLevel >= userHierarchyLevel;
  }

  private async checkOwnership(userId: string, organizationId: string): Promise<boolean> {
    const membership = await this.getUserMembership(userId, organizationId);
    return membership?.role === OrganizationRole.OWNER;
  }

  private async isLastOwner(userId: string, organizationId: string): Promise<boolean> {
    const ownerCount = await db('user_organizations')
      .where('organization_id', organizationId)
      .where('role', OrganizationRole.OWNER)
      .where('status', 'active')
      .whereNot('user_id', userId)
      .count('* as count')
      .first();

    return parseInt(ownerCount.count) === 0;
  }

  // Emit event for audit trail
  async emitEvent(eventType: string, data: any): Promise<void> {
    // Implementation would emit event to events table
    logger.info('Role event emitted', { eventType, data });
  }
}
```

## Files to Create

1. `src/models/organization-roles.ts` - Role and permission definitions
2. `src/controllers/role-controller.ts` - Role management endpoints
3. `src/services/role-service.ts` - Role business logic
4. `src/middleware/role-permission-middleware.ts` - Permission checking middleware
5. `src/routes/role-routes.ts` - Role management routes

## Dependencies

- Express.js for controllers and middleware
- Database connection for persistence
- Authorization service for integration
- Event service for audit logging

## Testing

1. Role assignment and validation tests
2. Permission checking middleware tests
3. Role hierarchy tests
4. Cross-role permission tests
5. Security tests for privilege escalation

## Notes

- Implement principle of least privilege
- Cache user permissions for performance
- Log all role changes for audit
- Consider role expiration and renewal
- Add comprehensive permission documentation
- Test role hierarchy thoroughly to prevent privilege escalation
