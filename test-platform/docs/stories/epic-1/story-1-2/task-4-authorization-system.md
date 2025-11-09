# Implementation Plan: Task 4 - Authorization System

**Story**: 1.2 Authentication & Authorization System  
**Task**: 4 - Authorization System  
**Acceptance Criteria**: #8 - Role-based access control (RBAC) with fine-grained permissions

## Overview

Implement comprehensive role-based access control system with fine-grained permissions, organization-scoped access, and flexible permission management.

## Implementation Steps

### Subtask 4.1: Define role-based permission model (Owner, Admin, Member, Viewer)

**Objective**: Create comprehensive RBAC model with roles and permissions

**File**: `src/models/permissions.ts`

```typescript
export enum Role {
  SUPER_ADMIN = 'super_admin',
  ORG_OWNER = 'owner',
  ORG_ADMIN = 'admin',
  ORG_MEMBER = 'member',
  ORG_VIEWER = 'viewer',
}

export enum Permission {
  // System permissions (super_admin only)
  SYSTEM_ADMIN = 'system.admin',
  SYSTEM_USER_MANAGEMENT = 'system.user_management',
  SYSTEM_ORGANIZATION_MANAGEMENT = 'system.organization_management',
  SYSTEM_AUDIT_LOGS = 'system.audit_logs',
  SYSTEM_METRICS = 'system.metrics',

  // Organization permissions
  ORG_CREATE = 'organization.create',
  ORG_READ = 'organization.read',
  ORG_UPDATE = 'organization.update',
  ORG_DELETE = 'organization.delete',
  ORG_MANAGE_SETTINGS = 'organization.manage_settings',
  ORG_MANAGE_BILLING = 'organization.manage_billing',
  ORG_MANAGE_MEMBERS = 'organization.manage_members',
  ORG_VIEW_AUDIT_LOGS = 'organization.view_audit_logs',

  // User management permissions
  USER_INVITE = 'user.invite',
  USER_READ = 'user.read',
  USER_UPDATE = 'user.update',
  USER_DELETE = 'user.delete',
  USER_MANAGE_ROLES = 'user.manage_roles',
  USER_DEACTIVATE = 'user.deactivate',

  // Benchmark permissions
  BENCHMARK_CREATE = 'benchmark.create',
  BENCHMARK_READ = 'benchmark.read',
  BENCHMARK_UPDATE = 'benchmark.update',
  BENCHMARK_DELETE = 'benchmark.delete',
  BENCHMARK_RUN = 'benchmark.run',
  BENCHMARK_VIEW_RESULTS = 'benchmark.view_results',
  BENCHMARK_EXPORT = 'benchmark.export',

  // API permissions
  API_CREATE_KEYS = 'api.create_keys',
  API_READ_KEYS = 'api.read_keys',
  API_UPDATE_KEYS = 'api.update_keys',
  API_DELETE_KEYS = 'api.delete_keys',
  API_VIEW_USAGE = 'api.view_usage',

  // Data permissions
  DATA_UPLOAD = 'data.upload',
  DATA_READ = 'data.read',
  DATA_UPDATE = 'data.update',
  DATA_DELETE = 'data.delete',
  DATA_EXPORT = 'data.export',
  DATA_IMPORT = 'data.import',
}

export interface RolePermissions {
  [Role.SUPER_ADMIN]: Permission[];
  [Role.ORG_OWNER]: Permission[];
  [Role.ORG_ADMIN]: Permission[];
  [Role.ORG_MEMBER]: Permission[];
  [Role.ORG_VIEWER]: Permission[];
}

export const ROLE_PERMISSIONS: RolePermissions = {
  [Role.SUPER_ADMIN]: [
    // All system permissions
    Permission.SYSTEM_ADMIN,
    Permission.SYSTEM_USER_MANAGEMENT,
    Permission.SYSTEM_ORGANIZATION_MANAGEMENT,
    Permission.SYSTEM_AUDIT_LOGS,
    Permission.SYSTEM_METRICS,

    // All organization permissions
    Permission.ORG_CREATE,
    Permission.ORG_READ,
    Permission.ORG_UPDATE,
    Permission.ORG_DELETE,
    Permission.ORG_MANAGE_SETTINGS,
    Permission.ORG_MANAGE_BILLING,
    Permission.ORG_MANAGE_MEMBERS,
    Permission.ORG_VIEW_AUDIT_LOGS,

    // All user permissions
    Permission.USER_INVITE,
    Permission.USER_READ,
    Permission.USER_UPDATE,
    Permission.USER_DELETE,
    Permission.USER_MANAGE_ROLES,
    Permission.USER_DEACTIVATE,

    // All benchmark permissions
    Permission.BENCHMARK_CREATE,
    Permission.BENCHMARK_READ,
    Permission.BENCHMARK_UPDATE,
    Permission.BENCHMARK_DELETE,
    Permission.BENCHMARK_RUN,
    Permission.BENCHMARK_VIEW_RESULTS,
    Permission.BENCHMARK_EXPORT,

    // All API permissions
    Permission.API_CREATE_KEYS,
    Permission.API_READ_KEYS,
    Permission.API_UPDATE_KEYS,
    Permission.API_DELETE_KEYS,
    Permission.API_VIEW_USAGE,

    // All data permissions
    Permission.DATA_UPLOAD,
    Permission.DATA_READ,
    Permission.DATA_UPDATE,
    Permission.DATA_DELETE,
    Permission.DATA_EXPORT,
    Permission.DATA_IMPORT,
  ],

  [Role.ORG_OWNER]: [
    // Organization permissions
    Permission.ORG_READ,
    Permission.ORG_UPDATE,
    Permission.ORG_MANAGE_SETTINGS,
    Permission.ORG_MANAGE_BILLING,
    Permission.ORG_MANAGE_MEMBERS,
    Permission.ORG_VIEW_AUDIT_LOGS,

    // User permissions
    Permission.USER_INVITE,
    Permission.USER_READ,
    Permission.USER_UPDATE,
    Permission.USER_DELETE,
    Permission.USER_MANAGE_ROLES,
    Permission.USER_DEACTIVATE,

    // Benchmark permissions
    Permission.BENCHMARK_CREATE,
    Permission.BENCHMARK_READ,
    Permission.BENCHMARK_UPDATE,
    Permission.BENCHMARK_DELETE,
    Permission.BENCHMARK_RUN,
    Permission.BENCHMARK_VIEW_RESULTS,
    Permission.BENCHMARK_EXPORT,

    // API permissions
    Permission.API_CREATE_KEYS,
    Permission.API_READ_KEYS,
    Permission.API_UPDATE_KEYS,
    Permission.API_DELETE_KEYS,
    Permission.API_VIEW_USAGE,

    // Data permissions
    Permission.DATA_UPLOAD,
    Permission.DATA_READ,
    Permission.DATA_UPDATE,
    Permission.DATA_DELETE,
    Permission.DATA_EXPORT,
    Permission.DATA_IMPORT,
  ],

  [Role.ORG_ADMIN]: [
    // Organization permissions
    Permission.ORG_READ,
    Permission.ORG_UPDATE,
    Permission.ORG_MANAGE_MEMBERS,
    Permission.ORG_VIEW_AUDIT_LOGS,

    // User permissions
    Permission.USER_INVITE,
    Permission.USER_READ,
    Permission.USER_UPDATE,
    Permission.USER_DEACTIVATE,

    // Benchmark permissions
    Permission.BENCHMARK_CREATE,
    Permission.BENCHMARK_READ,
    Permission.BENCHMARK_UPDATE,
    Permission.BENCHMARK_DELETE,
    Permission.BENCHMARK_RUN,
    Permission.BENCHMARK_VIEW_RESULTS,
    Permission.BENCHMARK_EXPORT,

    // API permissions
    Permission.API_CREATE_KEYS,
    Permission.API_READ_KEYS,
    Permission.API_UPDATE_KEYS,
    Permission.API_DELETE_KEYS,
    Permission.API_VIEW_USAGE,

    // Data permissions
    Permission.DATA_UPLOAD,
    Permission.DATA_READ,
    Permission.DATA_UPDATE,
    Permission.DATA_DELETE,
    Permission.DATA_EXPORT,
    Permission.DATA_IMPORT,
  ],

  [Role.ORG_MEMBER]: [
    // Organization permissions
    Permission.ORG_READ,

    // User permissions
    Permission.USER_READ,

    // Benchmark permissions
    Permission.BENCHMARK_CREATE,
    Permission.BENCHMARK_READ,
    Permission.BENCHMARK_UPDATE,
    Permission.BENCHMARK_DELETE,
    Permission.BENCHMARK_RUN,
    Permission.BENCHMARK_VIEW_RESULTS,
    Permission.BENCHMARK_EXPORT,

    // API permissions
    Permission.API_CREATE_KEYS,
    Permission.API_READ_KEYS,
    Permission.API_UPDATE_KEYS,
    Permission.API_DELETE_KEYS,
    Permission.API_VIEW_USAGE,

    // Data permissions
    Permission.DATA_UPLOAD,
    Permission.DATA_READ,
    Permission.DATA_UPDATE,
    Permission.DATA_DELETE,
    Permission.DATA_EXPORT,
  ],

  [Role.ORG_VIEWER]: [
    // Organization permissions
    Permission.ORG_READ,

    // User permissions
    Permission.USER_READ,

    // Benchmark permissions
    Permission.BENCHMARK_READ,
    Permission.BENCHMARK_VIEW_RESULTS,
    Permission.BENCHMARK_EXPORT,

    // API permissions
    Permission.API_READ_KEYS,
    Permission.API_VIEW_USAGE,

    // Data permissions
    Permission.DATA_READ,
    Permission.DATA_EXPORT,
  ],
};

export interface PermissionContext {
  userId: string;
  organizationId?: string;
  role?: Role;
  permissions?: Permission[];
}

export interface ResourceContext {
  resourceType: string;
  resourceId?: string;
  organizationId?: string;
  ownerId?: string;
}
```

### Subtask 4.2: Implement permission checking middleware

**Objective**: Create flexible permission checking middleware

**File**: `src/services/authorization-service.ts`

```typescript
import {
  Permission,
  Role,
  ROLE_PERMISSIONS,
  PermissionContext,
  ResourceContext,
} from '../models/permissions';
import { db } from '../database/connection';
import { logger } from '../observability/logger';
import { ApiError } from '../utils/api-error';

export class AuthorizationService {
  async getUserPermissions(
    userId: string,
    organizationId?: string
  ): Promise<{
    role: Role;
    permissions: Permission[];
    organizationId?: string;
  }> {
    try {
      // Get user's system role
      const user = await db('users').where('id', userId).where('status', 'active').first();

      if (!user) {
        throw new ApiError(404, 'User not found');
      }

      // Super admin has all permissions system-wide
      if (user.role === Role.SUPER_ADMIN) {
        return {
          role: Role.SUPER_ADMIN,
          permissions: ROLE_PERMISSIONS[Role.SUPER_ADMIN],
        };
      }

      // If no organization context, return minimal permissions
      if (!organizationId) {
        return {
          role: Role.ORG_VIEWER,
          permissions: [],
        };
      }

      // Get user's organization role
      const userOrg = await db('user_organizations')
        .where('user_id', userId)
        .where('organization_id', organizationId)
        .where('status', 'active')
        .first();

      if (!userOrg) {
        throw new ApiError(403, 'User is not a member of this organization');
      }

      const role = userOrg.role as Role;
      const permissions = ROLE_PERMISSIONS[role] || [];

      // Add any additional permissions from the user_organization record
      const additionalPermissions = userOrg.permissions ? JSON.parse(userOrg.permissions) : [];
      const allPermissions = [...new Set([...permissions, ...additionalPermissions])];

      return {
        role,
        permissions: allPermissions,
        organizationId,
      };
    } catch (error) {
      if (error instanceof ApiError) {
        throw error;
      }
      logger.error('Failed to get user permissions', { error, userId, organizationId });
      throw new ApiError(500, 'Failed to get user permissions');
    }
  }

  async hasPermission(
    userId: string,
    permission: Permission,
    organizationId?: string,
    resourceContext?: ResourceContext
  ): Promise<boolean> {
    try {
      const userPermissions = await this.getUserPermissions(userId, organizationId);

      // Check if user has the required permission
      if (!userPermissions.permissions.includes(permission)) {
        return false;
      }

      // Additional resource-specific checks
      if (resourceContext) {
        return await this.checkResourceAccess(userId, userPermissions, resourceContext);
      }

      return true;
    } catch (error) {
      logger.error('Permission check failed', { error, userId, permission, organizationId });
      return false;
    }
  }

  async requirePermission(
    userId: string,
    permission: Permission,
    organizationId?: string,
    resourceContext?: ResourceContext
  ): Promise<void> {
    const hasPermission = await this.hasPermission(
      userId,
      permission,
      organizationId,
      resourceContext
    );

    if (!hasPermission) {
      throw new ApiError(403, 'Insufficient permissions', {
        requiredPermission: permission,
        organizationId,
        resourceType: resourceContext?.resourceType,
      });
    }
  }

  async hasAnyPermission(
    userId: string,
    permissions: Permission[],
    organizationId?: string,
    resourceContext?: ResourceContext
  ): Promise<boolean> {
    for (const permission of permissions) {
      if (await this.hasPermission(userId, permission, organizationId, resourceContext)) {
        return true;
      }
    }
    return false;
  }

  async requireAnyPermission(
    userId: string,
    permissions: Permission[],
    organizationId?: string,
    resourceContext?: ResourceContext
  ): Promise<void> {
    const hasPermission = await this.hasAnyPermission(
      userId,
      permissions,
      organizationId,
      resourceContext
    );

    if (!hasPermission) {
      throw new ApiError(403, 'Insufficient permissions', {
        requiredPermissions: permissions,
        organizationId,
        resourceType: resourceContext?.resourceType,
      });
    }
  }

  async hasAllPermissions(
    userId: string,
    permissions: Permission[],
    organizationId?: string,
    resourceContext?: ResourceContext
  ): Promise<boolean> {
    for (const permission of permissions) {
      if (!(await this.hasPermission(userId, permission, organizationId, resourceContext))) {
        return false;
      }
    }
    return true;
  }

  async requireAllPermissions(
    userId: string,
    permissions: Permission[],
    organizationId?: string,
    resourceContext?: ResourceContext
  ): Promise<void> {
    const hasPermission = await this.hasAllPermissions(
      userId,
      permissions,
      organizationId,
      resourceContext
    );

    if (!hasPermission) {
      throw new ApiError(403, 'Insufficient permissions', {
        requiredPermissions: permissions,
        organizationId,
        resourceType: resourceContext?.resourceType,
      });
    }
  }

  private async checkResourceAccess(
    userId: string,
    userPermissions: { role: Role; permissions: Permission[] },
    resourceContext: ResourceContext
  ): Promise<boolean> {
    try {
      switch (resourceContext.resourceType) {
        case 'benchmark':
          return await this.checkBenchmarkAccess(userId, userPermissions, resourceContext);
        case 'api_key':
          return await this.checkApiKeyAccess(userId, userPermissions, resourceContext);
        case 'user':
          return await this.checkUserAccess(userId, userPermissions, resourceContext);
        case 'organization':
          return await this.checkOrganizationAccess(userId, userPermissions, resourceContext);
        default:
          return true; // No additional checks for unknown resource types
      }
    } catch (error) {
      logger.error('Resource access check failed', { error, userId, resourceContext });
      return false;
    }
  }

  private async checkBenchmarkAccess(
    userId: string,
    userPermissions: { role: Role; permissions: Permission[] },
    resourceContext: ResourceContext
  ): Promise<boolean> {
    if (!resourceContext.resourceId) {
      return true;
    }

    // Get benchmark owner
    const benchmark = await db('benchmarks').where('id', resourceContext.resourceId).first();

    if (!benchmark) {
      return false;
    }

    // Owner can always access their own benchmarks
    if (benchmark.created_by === userId) {
      return true;
    }

    // Otherwise, check organization permissions
    return userPermissions.organizationId === benchmark.organization_id;
  }

  private async checkApiKeyAccess(
    userId: string,
    userPermissions: { role: Role; permissions: Permission[] },
    resourceContext: ResourceContext
  ): Promise<boolean> {
    if (!resourceContext.resourceId) {
      return true;
    }

    // Get API key owner
    const apiKey = await db('api_keys').where('id', resourceContext.resourceId).first();

    if (!apiKey) {
      return false;
    }

    // Owner can always access their own API keys
    if (apiKey.user_id === userId) {
      return true;
    }

    // Admins and owners can manage organization API keys
    if ([Role.ORG_OWNER, Role.ORG_ADMIN].includes(userPermissions.role)) {
      return apiKey.organization_id === userPermissions.organizationId;
    }

    return false;
  }

  private async checkUserAccess(
    userId: string,
    userPermissions: { role: Role; permissions: Permission[] },
    resourceContext: ResourceContext
  ): Promise<boolean> {
    if (!resourceContext.resourceId) {
      return true;
    }

    // Users can always access their own profile
    if (resourceContext.resourceId === userId) {
      return true;
    }

    // Otherwise, check if they can manage users in the organization
    if (!userPermissions.permissions.includes(Permission.USER_READ)) {
      return false;
    }

    // Check if target user is in the same organization
    const targetUserOrg = await db('user_organizations')
      .where('user_id', resourceContext.resourceId)
      .where('organization_id', userPermissions.organizationId)
      .first();

    return !!targetUserOrg;
  }

  private async checkOrganizationAccess(
    userId: string,
    userPermissions: { role: Role; permissions: Permission[] },
    resourceContext: ResourceContext
  ): Promise<boolean> {
    if (!resourceContext.resourceId) {
      return true;
    }

    // Check if user is member of the organization
    const userOrg = await db('user_organizations')
      .where('user_id', userId)
      .where('organization_id', resourceContext.resourceId)
      .where('status', 'active')
      .first();

    return !!userOrg;
  }
}
```

### Subtask 4.3: Create role assignment and management endpoints

**Objective**: Build endpoints for role and permission management

**File**: `src/controllers/authorization-controller.ts`

```typescript
import { Request, Response } from 'express';
import { body, param, validationResult } from 'express-validator';
import { AuthorizationService } from '../services/authorization-service';
import { Role, Permission } from '../models/permissions';
import { logger } from '../observability/logger';
import { ApiError } from '../utils/api-error';

export class AuthorizationController {
  constructor(private authService: AuthorizationService) {}

  // Validation middleware
  static assignRoleValidation = [
    param('userId').isUUID().withMessage('Valid user ID is required'),
    param('organizationId').isUUID().withMessage('Valid organization ID is required'),
    body('role').isIn(Object.values(Role)).withMessage('Valid role is required'),
    body('permissions').optional().isArray().withMessage('Permissions must be an array'),
    body('permissions.*')
      .optional()
      .isIn(Object.values(Permission))
      .withMessage('Each permission must be valid'),
  ];

  async assignRole(req: Request, res: Response): Promise<void> {
    try {
      // Check validation errors
      const errors = validationResult(req);
      if (!errors.isEmpty()) {
        throw new ApiError(400, 'Validation failed', errors.array());
      }

      const { userId, organizationId } = req.params;
      const { role, permissions } = req.body;
      const currentUserId = req.user!.sub;

      // Check if current user can manage roles in this organization
      await this.authService.requirePermission(
        currentUserId,
        Permission.USER_MANAGE_ROLES,
        organizationId
      );

      // Update user's role in organization
      await this.updateUserRole(userId, organizationId, role, permissions);

      // Emit event for audit trail
      await this.emitEvent('USER_ROLE_ASSIGNED', {
        userId,
        organizationId,
        role,
        permissions,
        assignedBy: currentUserId,
        ipAddress: req.ip,
        userAgent: req.get('User-Agent'),
      });

      logger.info('User role assigned', {
        userId,
        organizationId,
        role,
        assignedBy: currentUserId,
      });

      res.json({
        message: 'Role assigned successfully',
        userRole: {
          userId,
          organizationId,
          role,
          permissions,
        },
      });
    } catch (error) {
      if (error instanceof ApiError) {
        res.status(error.statusCode).json({
          error: error.message,
          details: error.details,
        });
      } else {
        logger.error('Role assignment error', { error });
        res.status(500).json({ error: 'Internal server error' });
      }
    }
  }

  async getUserPermissions(req: Request, res: Response): Promise<void> {
    try {
      const { userId } = req.params;
      const { organizationId } = req.query;
      const currentUserId = req.user!.sub;

      // Users can only view their own permissions unless they have admin privileges
      if (userId !== currentUserId) {
        if (organizationId) {
          await this.authService.requirePermission(
            currentUserId,
            Permission.USER_READ,
            organizationId as string
          );
        } else {
          await this.authService.requirePermission(
            currentUserId,
            Permission.SYSTEM_USER_MANAGEMENT
          );
        }
      }

      const userPermissions = await this.authService.getUserPermissions(
        userId,
        organizationId as string
      );

      res.json({
        user: userId,
        organizationId,
        role: userPermissions.role,
        permissions: userPermissions.permissions,
      });
    } catch (error) {
      if (error instanceof ApiError) {
        res.status(error.statusCode).json({
          error: error.message,
        });
      } else {
        logger.error('Get user permissions error', { error });
        res.status(500).json({ error: 'Internal server error' });
      }
    }
  }

  async getOrganizationMembers(req: Request, res: Response): Promise<void> {
    try {
      const { organizationId } = req.params;
      const currentUserId = req.user!.sub;

      // Check if user can read organization members
      await this.authService.requirePermission(currentUserId, Permission.USER_READ, organizationId);

      const members = await this.getOrganizationMembersList(organizationId);

      res.json({
        organizationId,
        members,
      });
    } catch (error) {
      if (error instanceof ApiError) {
        res.status(error.statusCode).json({
          error: error.message,
        });
      } else {
        logger.error('Get organization members error', { error });
        res.status(500).json({ error: 'Internal server error' });
      }
    }
  }

  async removeUserFromOrganization(req: Request, res: Response): Promise<void> {
    try {
      const { userId, organizationId } = req.params;
      const currentUserId = req.user!.sub;

      // Check if current user can remove users from organization
      await this.authService.requirePermission(
        currentUserId,
        Permission.USER_DEACTIVATE,
        organizationId
      );

      // Remove user from organization
      await this.removeUserFromOrg(userId, organizationId);

      // Emit event for audit trail
      await this.emitEvent('USER_REMOVED_FROM_ORGANIZATION', {
        userId,
        organizationId,
        removedBy: currentUserId,
        ipAddress: req.ip,
        userAgent: req.get('User-Agent'),
      });

      logger.info('User removed from organization', {
        userId,
        organizationId,
        removedBy: currentUserId,
      });

      res.json({
        message: 'User removed from organization successfully',
      });
    } catch (error) {
      if (error instanceof ApiError) {
        res.status(error.statusCode).json({
          error: error.message,
        });
      } else {
        logger.error('Remove user from organization error', { error });
        res.status(500).json({ error: 'Internal server error' });
      }
    }
  }

  private async updateUserRole(
    userId: string,
    organizationId: string,
    role: Role,
    permissions?: Permission[]
  ): Promise<void> {
    await db('user_organizations')
      .where('user_id', userId)
      .where('organization_id', organizationId)
      .update({
        role,
        permissions: permissions ? JSON.stringify(permissions) : null,
        updated_at: new Date(),
      });
  }

  private async getOrganizationMembersList(organizationId: string): Promise<any[]> {
    return await db('user_organizations')
      .join('users', 'user_organizations.user_id', 'users.id')
      .where('user_organizations.organization_id', organizationId)
      .where('user_organizations.status', 'active')
      .select([
        'users.id',
        'users.email',
        'users.first_name',
        'users.last_name',
        'users.status as user_status',
        'user_organizations.role',
        'user_organizations.permissions',
        'user_organizations.joined_at',
        'user_organizations.status as membership_status',
      ])
      .orderBy('user_organizations.joined_at', 'asc');
  }

  private async removeUserFromOrg(userId: string, organizationId: string): Promise<void> {
    await db('user_organizations')
      .where('user_id', userId)
      .where('organization_id', organizationId)
      .update({
        status: 'left',
        left_at: new Date(),
        updated_at: new Date(),
      });
  }

  private async emitEvent(eventType: string, data: any): Promise<void> {
    // Implementation would emit event to events table
    logger.info('Authorization event emitted', { eventType, data });
  }
}
```

### Subtask 4.4: Implement organization-scoped permissions

**Objective**: Create middleware for organization-scoped permission checking

**File**: `src/middleware/authorization-middleware.ts`

```typescript
import { Request, Response, NextFunction } from 'express';
import { AuthorizationService } from '../services/authorization-service';
import { Permission, Role } from '../models/permissions';
import { ApiError } from '../utils/api-error';

declare global {
  namespace Express {
    interface Request {
      user?: {
        sub: string;
        email: string;
        organizationId?: string;
        role?: Role;
        permissions?: Permission[];
      };
    }
  }
}

export class AuthorizationMiddleware {
  constructor(private authService: AuthorizationService) {}

  // Middleware to check if user has specific permission
  requirePermission(permission: Permission, getOrganizationId?: (req: Request) => string) {
    return async (req: Request, res: Response, next: NextFunction): Promise<void> => {
      try {
        const userId = req.user?.sub;
        if (!userId) {
          throw new ApiError(401, 'Authentication required');
        }

        const organizationId = getOrganizationId
          ? getOrganizationId(req)
          : req.user?.organizationId;

        await this.authService.requirePermission(userId, permission, organizationId);

        next();
      } catch (error) {
        if (error instanceof ApiError) {
          res.status(error.statusCode).json({
            error: error.message,
            details: error.details,
          });
        } else {
          res.status(500).json({ error: 'Authorization check failed' });
        }
      }
    };
  }

  // Middleware to check if user has any of the specified permissions
  requireAnyPermission(permissions: Permission[], getOrganizationId?: (req: Request) => string) {
    return async (req: Request, res: Response, next: NextFunction): Promise<void> => {
      try {
        const userId = req.user?.sub;
        if (!userId) {
          throw new ApiError(401, 'Authentication required');
        }

        const organizationId = getOrganizationId
          ? getOrganizationId(req)
          : req.user?.organizationId;

        await this.authService.requireAnyPermission(userId, permissions, organizationId);

        next();
      } catch (error) {
        if (error instanceof ApiError) {
          res.status(error.statusCode).json({
            error: error.message,
            details: error.details,
          });
        } else {
          res.status(500).json({ error: 'Authorization check failed' });
        }
      }
    };
  }

  // Middleware to check if user has all specified permissions
  requireAllPermissions(permissions: Permission[], getOrganizationId?: (req: Request) => string) {
    return async (req: Request, res: Response, next: NextFunction): Promise<void> => {
      try {
        const userId = req.user?.sub;
        if (!userId) {
          throw new ApiError(401, 'Authentication required');
        }

        const organizationId = getOrganizationId
          ? getOrganizationId(req)
          : req.user?.organizationId;

        await this.authService.requireAllPermissions(userId, permissions, organizationId);

        next();
      } catch (error) {
        if (error instanceof ApiError) {
          res.status(error.statusCode).json({
            error: error.message,
            details: error.details,
          });
        } else {
          res.status(500).json({ error: 'Authorization check failed' });
        }
      }
    };
  }

  // Middleware to check resource ownership or admin access
  requireOwnershipOrAdmin(
    resourceType: string,
    getResourceOwnerId: (req: Request) => Promise<string | undefined>,
    getOrganizationId?: (req: Request) => string
  ) {
    return async (req: Request, res: Response, next: NextFunction): Promise<void> => {
      try {
        const userId = req.user?.sub;
        if (!userId) {
          throw new ApiError(401, 'Authentication required');
        }

        const organizationId = getOrganizationId
          ? getOrganizationId(req)
          : req.user?.organizationId;
        const resourceOwnerId = await getResourceOwnerId(req);

        // Check if user owns the resource or has admin permissions
        const isOwner = resourceOwnerId === userId;
        const isAdmin = await this.authService.hasPermission(
          userId,
          Permission.USER_MANAGE_ROLES,
          organizationId
        );

        if (!isOwner && !isAdmin) {
          throw new ApiError(403, 'Access denied: must be resource owner or admin');
        }

        next();
      } catch (error) {
        if (error instanceof ApiError) {
          res.status(error.statusCode).json({
            error: error.message,
            details: error.details,
          });
        } else {
          res.status(500).json({ error: 'Authorization check failed' });
        }
      }
    };
  }

  // Middleware to load user permissions into request
  loadPermissions(getOrganizationId?: (req: Request) => string) {
    return async (req: Request, res: Response, next: NextFunction): Promise<void> => {
      try {
        const userId = req.user?.sub;
        if (!userId) {
          return next();
        }

        const organizationId = getOrganizationId
          ? getOrganizationId(req)
          : req.user?.organizationId;

        const userPermissions = await this.authService.getUserPermissions(userId, organizationId);

        // Update request user object with permissions
        if (req.user) {
          req.user.organizationId = userPermissions.organizationId;
          req.user.role = userPermissions.role;
          req.user.permissions = userPermissions.permissions;
        }

        next();
      } catch (error) {
        // Don't fail the request if permission loading fails, just log it
        console.error('Failed to load user permissions:', error);
        next();
      }
    };
  }

  // Middleware to check organization membership
  requireOrganizationMembership(getOrganizationId: (req: Request) => string) {
    return async (req: Request, res: Response, next: NextFunction): Promise<void> => {
      try {
        const userId = req.user?.sub;
        if (!userId) {
          throw new ApiError(401, 'Authentication required');
        }

        const organizationId = getOrganizationId(req);

        await this.authService.requirePermission(userId, Permission.ORG_READ, organizationId);

        next();
      } catch (error) {
        if (error instanceof ApiError) {
          res.status(error.statusCode).json({
            error: error.message,
            details: error.details,
          });
        } else {
          res.status(500).json({ error: 'Authorization check failed' });
        }
      }
    };
  }
}

// Helper functions for common permission checks
export const requireOrgAdmin = (authService: AuthorizationService) =>
  authService.requirePermission(Permission.USER_MANAGE_ROLES);

export const requireOrgMember = (authService: AuthorizationService) =>
  authService.requirePermission(Permission.ORG_READ);

export const requireBenchmarkCreate = (authService: AuthorizationService) =>
  authService.requirePermission(Permission.BENCHMARK_CREATE);

export const requireBenchmarkRead = (authService: AuthorizationService) =>
  authService.requirePermission(Permission.BENCHMARK_READ);

export const requireApiKeyManagement = (authService: AuthorizationService) =>
  authService.requirePermission(Permission.API_CREATE_KEYS);
```

## Files to Create

1. `src/models/permissions.ts` - Permission and role definitions
2. `src/services/authorization-service.ts` - Authorization logic
3. `src/controllers/authorization-controller.ts` - Role management endpoints
4. `src/middleware/authorization-middleware.ts` - Authorization middleware
5. Update existing controllers to use authorization middleware

## Dependencies

- Express.js for middleware
- Database connection for user role lookups
- JWT service for user authentication
- Event service for audit logging

## Testing

1. Permission checking unit tests
2. Role assignment tests
3. Organization membership tests
4. Resource ownership tests
5. Authorization middleware integration tests

## Notes

- Implement principle of least privilege
- Cache user permissions for performance
- Log all authorization decisions for audit
- Support custom permissions per organization
- Consider implementing permission inheritance
- Add permission checking to all sensitive endpoints
