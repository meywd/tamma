/**
 * Database Type Definitions
 *
 * These interfaces represent the structure of database tables
 * and are used throughout the application for type safety.
 */

export interface Organization {
  id: string;
  name: string;
  slug: string;
  description?: string;
  domain?: string;
  email?: string;
  phone?: string;
  website?: string;
  address_line1?: string;
  address_line2?: string;
  city?: string;
  state?: string;
  country?: string;
  postal_code?: string;
  settings: Record<string, any>;
  metadata: Record<string, any>;
  status: 'active' | 'inactive' | 'suspended' | 'deleted';
  subscription_tier: string;
  subscription_expires_at?: Date;
  created_at: Date;
  updated_at: Date;
  created_by?: string;
  updated_by?: string;
}

export interface User {
  id: string;
  email: string;
  password_hash: string;
  password_salt: string;
  password_reset_token?: string;
  password_reset_expires_at?: Date;
  email_verification_token?: string;
  email_verified_at?: Date;
  first_name?: string;
  last_name?: string;
  username?: string;
  bio?: string;
  avatar_url?: string;
  preferences: Record<string, any>;
  settings: Record<string, any>;
  timezone: string;
  language: string;
  status: 'active' | 'inactive' | 'suspended' | 'deleted';
  role: 'user' | 'admin' | 'super_admin';
  email_verified: boolean;
  mfa_enabled: boolean;
  mfa_secret?: string;
  last_login_ip?: string;
  current_login_ip?: string;
  last_login_at?: Date;
  current_login_at?: Date;
  failed_login_attempts: number;
  locked_until?: Date;
  created_at: Date;
  updated_at: Date;
  created_by?: string;
  updated_by?: string;
}

export interface UserOrganization {
  id: string;
  user_id: string;
  organization_id: string;
  role: 'member' | 'admin' | 'owner';
  permissions: Record<string, any>;
  status: 'active' | 'pending' | 'invited' | 'left';
  invitation_token?: string;
  invitation_expires_at?: Date;
  invited_by?: string;
  job_title?: string;
  department?: string;
  metadata: Record<string, any>;
  joined_at?: Date;
  left_at?: Date;
  created_at: Date;
  updated_at: Date;
}

export interface Event {
  id: string;
  event_type: string;
  aggregate_type: string;
  aggregate_id: string;
  aggregate_version: string;
  data: Record<string, any>;
  metadata: Record<string, any>;
  tags: Record<string, any>;
  user_id?: string;
  organization_id?: string;
  session_id?: string;
  correlation_id?: string;
  causation_id?: string;
  source: string;
  version?: string;
  ip_address?: string;
  user_agent?: string;
  event_timestamp: Date;
  created_at: Date;
  processed: boolean;
  processed_at?: Date;
  retry_count: number;
  error_message?: string;
}

export interface ApiKey {
  id: string;
  key_id: string;
  key_hash: string;
  key_prefix: string;
  user_id: string;
  organization_id?: string;
  name: string;
  description?: string;
  key_type: 'personal' | 'service' | 'integration';
  permissions: Record<string, any>;
  scopes: string[];
  allowed_ips?: string;
  allowed_domains?: string;
  status: 'active' | 'inactive' | 'revoked' | 'expired';
  expires_at?: Date;
  last_used_at?: Date;
  last_used_ip?: string;
  last_used_user_agent?: string;
  usage_count: number;
  usage_reset_at?: Date;
  usage_limit?: number;
  created_from_ip?: string;
  created_from_user_agent?: string;
  require_mfa: boolean;
  created_at: Date;
  updated_at: Date;
  created_by?: string;
  updated_by?: string;
}

/**
 * DCB (Dynamic Consistency Boundary) Event Types
 *
 * These types are specific to the event sourcing implementation
 * using the DCB pattern for maintaining consistency boundaries.
 */

export interface DCBEventBase {
  event_type: string;
  aggregate_type: string;
  aggregate_id: string;
  aggregate_version: string;
}

export interface DCBEventMetadata {
  user_id?: string;
  organization_id?: string;
  session_id?: string;
  correlation_id?: string;
  causation_id?: string;
  source: string;
  version?: string;
  ip_address?: string;
  user_agent?: string;
}

export interface DCBEventTags {
  [key: string]: string | number | boolean | null;
}

/**
 * Helper types for common database operations
 */

export type CreateOrganizationInput = Omit<Organization, 'id' | 'created_at' | 'updated_at'>;
export type UpdateOrganizationInput = Partial<CreateOrganizationInput>;

export type CreateUserInput = Omit<User, 'id' | 'created_at' | 'updated_at'>;
export type UpdateUserInput = Partial<Omit<CreateUserInput, 'email'>>;

export type CreateApiKeyInput = Omit<ApiKey, 'id' | 'created_at' | 'updated_at' | 'key_hash' | 'usage_count'>;

/**
 * Query filter types
 */

export interface OrganizationFilter {
  id?: string;
  slug?: string;
  status?: Organization['status'];
  subscription_tier?: string;
}

export interface UserFilter {
  id?: string;
  email?: string;
  username?: string;
  status?: User['status'];
  role?: User['role'];
  email_verified?: boolean;
}

export interface EventFilter {
  event_type?: string;
  aggregate_type?: string;
  aggregate_id?: string;
  user_id?: string;
  organization_id?: string;
  correlation_id?: string;
  processed?: boolean;
  from_timestamp?: Date;
  to_timestamp?: Date;
}

/**
 * Pagination types
 */

export interface PaginationParams {
  page?: number;
  limit?: number;
  sort_by?: string;
  sort_order?: 'asc' | 'desc';
}

export interface PaginatedResult<T> {
  data: T[];
  pagination: {
    page: number;
    limit: number;
    total: number;
    total_pages: number;
  };
}