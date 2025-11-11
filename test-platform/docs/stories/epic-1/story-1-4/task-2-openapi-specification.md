# Task 2: OpenAPI Specification

**Story**: 1.4 - Basic API Infrastructure Documentation  
**Task**: 2 - OpenAPI Specification  
**Priority**: Medium  
**Estimated Time**: 2 hours

## OpenAPI 3.1 Specification

````yaml
openapi: 3.1.0
info:
  title: Test Platform API
  description: |
    Comprehensive API for the Test Platform including organization management,
    user authentication, resource tracking, and analytics.

    ## Authentication

    The API uses JWT tokens for authentication. Include the token in the Authorization header:

    ```
    Authorization: Bearer <your_access_token>
    ```

    ## Rate Limiting

    API requests are rate limited based on your subscription tier. Rate limit headers are included in all responses.

    ## Pagination

    List endpoints support pagination using `page` and `limit` parameters.

    ## Errors

    All errors return a consistent format with error codes and messages.
  version: 1.0.0
  contact:
    name: Test Platform API Support
    email: api-support@testplatform.com
    url: https://testplatform.com/support
  license:
    name: MIT
    url: https://opensource.org/licenses/MIT
servers:
  - url: https://api.testplatform.com/v1
    description: Production server
  - url: https://api-staging.testplatform.com/v1
    description: Staging server
  - url: http://localhost:3000/v1
    description: Development server

# Security Schemes
security:
  - BearerAuth: []
  - OrganizationAuth: []

components:
  securitySchemes:
    BearerAuth:
      type: http
      scheme: bearer
      bearerFormat: JWT
      description: JWT access token obtained from authentication endpoints
    OrganizationAuth:
      type: apiKey
      in: header
      name: X-Organization-ID
      description: Organization ID for multi-tenant requests

  # Schemas
  schemas:
    # Core Types
    UUID:
      type: string
      format: uuid
      example: "550e8400-e29b-41d4-a716-446655440000"

    Timestamp:
      type: string
      format: date-time
      example: "2025-01-15T10:30:00Z"

    Email:
      type: string
      format: email
      example: "user@example.com"

    # Response Wrapper
    ApiResponse:
      type: object
      properties:
        data:
          oneOf:
            - type: object
            - type: array
            - type: 'null'
        meta:
          $ref: '#/components/schemas/ResponseMeta'
        errors:
          type: array
          items:
            $ref: '#/components/schemas/ApiError'

    ResponseMeta:
      type: object
      properties:
        pagination:
          $ref: '#/components/schemas/Pagination'
        timestamp:
          $ref: '#/components/schemas/Timestamp'
        requestId:
          type: string
          example: "req_123456789"

    Pagination:
      type: object
      properties:
        page:
          type: integer
          minimum: 1
          example: 1
        limit:
          type: integer
          minimum: 1
          maximum: 100
          example: 20
        total:
          type: integer
          minimum: 0
          example: 100
        totalPages:
          type: integer
          minimum: 0
          example: 5
        hasNext:
          type: boolean
          example: true
        hasPrev:
          type: boolean
          example: false

    ApiError:
      type: object
      required:
        - code
        - message
      properties:
        code:
          type: string
          example: "VALIDATION_ERROR"
        message:
          type: string
          example: "Invalid input data"
        field:
          type: string
          example: "email"
        details:
          type: string
          example: "Email format is invalid"

    # User Schemas
    User:
      type: object
      required:
        - id
        - email
        - name
        - createdAt
      properties:
        id:
          $ref: '#/components/schemas/UUID'
        email:
          $ref: '#/components/schemas/Email'
        name:
          type: string
          minLength: 1
          maxLength: 255
          example: "John Doe"
        avatar:
          type: string
          format: uri
          example: "https://example.com/avatar.jpg"
        isActive:
          type: boolean
          example: true
        lastLoginAt:
          $ref: '#/components/schemas/Timestamp'
        createdAt:
          $ref: '#/components/schemas/Timestamp'
        updatedAt:
          $ref: '#/components/schemas/Timestamp'

    UserCreate:
      type: object
      required:
        - email
        - name
        - password
      properties:
        email:
          $ref: '#/components/schemas/Email'
        name:
          type: string
          minLength: 1
          maxLength: 255
          example: "John Doe"
        password:
          type: string
          minLength: 8
          format: password
          example: "securePassword123"

    UserUpdate:
      type: object
      properties:
        name:
          type: string
          minLength: 1
          maxLength: 255
          example: "John Smith"
        avatar:
          type: string
          format: uri
          example: "https://example.com/new-avatar.jpg"

    # Organization Schemas
    Organization:
      type: object
      required:
        - id
        - name
        - slug
        - ownerId
        - createdAt
      properties:
        id:
          $ref: '#/components/schemas/UUID'
        name:
          type: string
          minLength: 1
          maxLength: 255
          example: "Acme Corp"
        slug:
          type: string
          pattern: '^[a-z0-9-]+$'
          example: "acme-corp"
        description:
          type: string
          maxLength: 1000
          example: "Technology company specializing in AI solutions"
        logo:
          type: string
          format: uri
          example: "https://example.com/logo.png"
        website:
          type: string
          format: uri
          example: "https://acme.com"
        ownerId:
          $ref: '#/components/schemas/UUID'
        settings:
          type: object
          example:
            allowInvitations: true
            defaultRole: "member"
        createdAt:
          $ref: '#/components/schemas/Timestamp'
        updatedAt:
          $ref: '#/components/schemas/Timestamp'

    OrganizationCreate:
      type: object
      required:
        - name
        - slug
      properties:
        name:
          type: string
          minLength: 1
          maxLength: 255
          example: "Acme Corp"
        slug:
          type: string
          pattern: '^[a-z0-9-]+$'
          example: "acme-corp"
        description:
          type: string
          maxLength: 1000
          example: "Technology company specializing in AI solutions"
        logo:
          type: string
          format: uri
          example: "https://example.com/logo.png"
        website:
          type: string
          format: uri
          example: "https://acme.com"
        settings:
          type: object
          example:
            allowInvitations: true
            defaultRole: "member"

    OrganizationUpdate:
      type: object
      properties:
        name:
          type: string
          minLength: 1
          maxLength: 255
          example: "Acme Corporation"
        description:
          type: string
          maxLength: 1000
          example: "Updated description"
        logo:
          type: string
          format: uri
          example: "https://example.com/new-logo.png"
        website:
          type: string
          format: uri
          example: "https://new-acme.com"
        settings:
          type: object
          example:
            allowInvitations: false
            defaultRole: "viewer"

    # Role Schemas
    Role:
      type: object
      required:
        - id
        - name
        - permissions
        - createdAt
      properties:
        id:
          $ref: '#/components/schemas/UUID'
        name:
          type: string
          minLength: 1
          maxLength: 100
          example: "Admin"
        description:
          type: string
          maxLength: 500
          example: "Full administrative access"
        permissions:
          type: array
          items:
            type: string
          example: ["user:read", "user:write", "org:manage"]
        isSystem:
          type: boolean
          example: false
        createdAt:
          $ref: '#/components/schemas/Timestamp'
        updatedAt:
          $ref: '#/components/schemas/Timestamp'

    RoleCreate:
      type: object
      required:
        - name
        - permissions
      properties:
        name:
          type: string
          minLength: 1
          maxLength: 100
          example: "Manager"
        description:
          type: string
          maxLength: 500
          example: "Can manage team members and projects"
        permissions:
          type: array
          items:
            type: string
          example: ["member:read", "member:write", "project:read"]

    # Member Schemas
    Member:
      type: object
      required:
        - id
        - organizationId
        - userId
        - email
        - name
        - status
        - roles
        - joinedAt
      properties:
        id:
          $ref: '#/components/schemas/UUID'
        organizationId:
          $ref: '#/components/schemas/UUID'
        userId:
          $ref: '#/components/schemas/UUID'
        email:
          $ref: '#/components/schemas/Email'
        name:
          type: string
          example: "John Doe"
        status:
          type: string
          enum: ["active", "suspended", "pending"]
          example: "active"
        roles:
          type: array
          items:
            $ref: '#/components/schemas/Role'
        joinedAt:
          $ref: '#/components/schemas/Timestamp'
        lastLoginAt:
          $ref: '#/components/schemas/Timestamp'

    MemberCreate:
      type: object
      required:
        - userId
      properties:
        userId:
          $ref: '#/components/schemas/UUID'
        roleIds:
          type: array
          items:
            $ref: '#/components/schemas/UUID'
          example: ["role-123", "role-456"]

    MemberUpdate:
      type: object
      properties:
        roleIds:
          type: array
          items:
            $ref: '#/components/schemas/UUID'
          example: ["role-789"]
        status:
          type: string
          enum: ["active", "suspended"]
          example: "suspended"

    # Invitation Schemas
    Invitation:
      type: object
      required:
        - id
        - organizationId
        - email
        - status
        - expiresAt
        - createdAt
      properties:
        id:
          $ref: '#/components/schemas/UUID'
        organizationId:
          $ref: '#/components/schemas/UUID'
        email:
          $ref: '#/components/schemas/Email'
        roleId:
          $ref: '#/components/schemas/UUID'
        status:
          type: string
          enum: ["pending", "accepted", "expired", "revoked"]
          example: "pending"
        expiresAt:
          $ref: '#/components/schemas/Timestamp'
        acceptedAt:
          $ref: '#/components/schemas/Timestamp'
        createdAt:
          $ref: '#/components/schemas/Timestamp'

    InvitationCreate:
      type: object
      required:
        - email
      properties:
        email:
          $ref: '#/components/schemas/Email'
        roleId:
          $ref: '#/components/schemas/UUID'
        message:
          type: string
          maxLength: 1000
          example: "Join our team to work on exciting projects!"

    # Authentication Schemas
    AuthLogin:
      type: object
      required:
        - email
        - password
      properties:
        email:
          $ref: '#/components/schemas/Email'
        password:
          type: string
          format: password
          example: "userPassword123"

    AuthRegister:
      type: object
      required:
        - email
        - password
        - name
      properties:
        email:
          $ref: '#/components/schemas/Email'
        password:
          type: string
          minLength: 8
          format: password
          example: "securePassword123"
        name:
          type: string
          minLength: 1
          maxLength: 255
          example: "John Doe"

    AuthResponse:
      type: object
      required:
        - user
        - accessToken
        - refreshToken
      properties:
        user:
          $ref: '#/components/schemas/User'
        accessToken:
          type: string
          example: "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..."
        refreshToken:
          type: string
          example: "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..."
        expiresIn:
          type: integer
          example: 3600

    AuthRefresh:
      type: object
      required:
        - refreshToken
      properties:
        refreshToken:
          type: string
          example: "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..."

    # Resource Quota Schemas
    ResourceQuota:
      type: object
      required:
        - resourceTypeId
        - resourceType
        - unit
        - quotaLimit
        - isUnlimited
        - currentUsage
        - usagePercentage
      properties:
        resourceTypeId:
          $ref: '#/components/schemas/UUID'
        resourceType:
          type: string
          example: "api_requests"
        unit:
          type: string
          example: "requests"
        quotaLimit:
          type: integer
          nullable: true
          example: 10000
        isUnlimited:
          type: boolean
          example: false
        currentUsage:
          type: integer
          example: 1500
        usagePercentage:
          type: number
          format: float
          example: 15.0
        remaining:
          type: integer
          nullable: true
          example: 8500

    QuotaCheck:
      type: object
      required:
        - resourceType
        - amount
      properties:
        resourceType:
          type: string
          example: "api_requests"
        amount:
          type: integer
          minimum: 1
          example: 100
        metadata:
          type: object
          example:
            endpoint: "/organizations"
            method: "GET"

    QuotaCheckResponse:
      type: object
      required:
        - allowed
        - quota
      properties:
        allowed:
          type: boolean
          example: true
        quota:
          $ref: '#/components/schemas/ResourceQuota'
        reason:
          type: string
          example: "Quota would be exceeded. Requested: 100, Available: 50"

    # Usage Analytics Schemas
    UsageReport:
      type: object
      required:
        - organizationId
        - period
        - totalUsage
        - costBreakdown
        - trends
        - recommendations
      properties:
        organizationId:
          $ref: '#/components/schemas/UUID'
        period:
          type: object
          required:
            - start
            - end
          properties:
            start:
              $ref: '#/components/schemas/Timestamp'
            end:
              $ref: '#/components/schemas/Timestamp'
        totalUsage:
          type: object
          additionalProperties:
            type: integer
          example:
            api_requests: 15000
            storage: 5368709120
        costBreakdown:
          type: object
          additionalProperties:
            type: number
            format: float
          example:
            api_requests: 1.50
            storage: 10.00
        trends:
          type: array
          items:
            $ref: '#/components/schemas/UsageTrend'
        recommendations:
          type: array
          items:
            type: string
          example:
            - "Consider upgrading to Pro tier for higher limits"
            - "API usage increased by 25% this month"

    UsageTrend:
      type: object
      required:
        - resourceType
        - currentUsage
        - previousUsage
        - growthRate
        - projectedUsage
        - costImpact
      properties:
        resourceType:
          type: string
          example: "api_requests"
        currentUsage:
          type: integer
          example: 15000
        previousUsage:
          type: integer
          example: 12000
        growthRate:
          type: number
          format: float
          example: 25.0
        projectedUsage:
          type: number
          format: float
          example: 18750
        costImpact:
          type: number
          format: float
          example: 1.50

    TopConsumer:
      type: object
      required:
        - userId
        - usage
        - percentage
      properties:
        userId:
          $ref: '#/components/schemas/UUID'
        usage:
          type: integer
          example: 5000
        percentage:
          type: number
          format: float
          example: 33.3

    UsageTrendData:
      type: object
      required:
        - date
        - usage
      properties:
        date:
          type: string
          format: date
          example: "2025-01-15"
        usage:
          type: integer
          example: 500

# Paths
paths:
  # Authentication endpoints
  /auth/register:
    post:
      tags:
        - Authentication
      summary: Register a new user
      description: Create a new user account and return authentication tokens
      requestBody:
        required: true
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/AuthRegister'
      responses:
        '201':
          description: User registered successfully
          content:
            application/json:
              schema:
                allOf:
                  - $ref: '#/components/schemas/ApiResponse'
                  - type: object
                    properties:
                      data:
                        $ref: '#/components/schemas/AuthResponse'
        '400':
          description: Validation error
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '409':
          description: User already exists
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

  /auth/login:
    post:
      tags:
        - Authentication
      summary: User login
      description: Authenticate user and return access tokens
      requestBody:
        required: true
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/AuthLogin'
      responses:
        '200':
          description: Login successful
          content:
            application/json:
              schema:
                allOf:
                  - $ref: '#/components/schemas/ApiResponse'
                  - type: object
                    properties:
                      data:
                        $ref: '#/components/schemas/AuthResponse'
        '401':
          description: Invalid credentials
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

  /auth/refresh:
    post:
      tags:
        - Authentication
      summary: Refresh access token
      description: Get new access token using refresh token
      requestBody:
        required: true
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/AuthRefresh'
      responses:
        '200':
          description: Token refreshed successfully
          content:
            application/json:
              schema:
                allOf:
                  - $ref: '#/components/schemas/ApiResponse'
                  - type: object
                    properties:
                      data:
                        type: object
                        properties:
                          accessToken:
                            type: string
                          expiresIn:
                            type: integer
        '401':
          description: Invalid refresh token
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

  /auth/logout:
    post:
      tags:
        - Authentication
      summary: User logout
      description: Invalidate current access token
      security:
        - BearerAuth: []
      responses:
        '200':
          description: Logout successful
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '401':
          description: Unauthorized
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

  # User endpoints
  /users/profile:
    get:
      tags:
        - Users
      summary: Get current user profile
      description: Retrieve the profile of the authenticated user
      security:
        - BearerAuth: []
      responses:
        '200':
          description: User profile retrieved successfully
          content:
            application/json:
              schema:
                allOf:
                  - $ref: '#/components/schemas/ApiResponse'
                  - type: object
                    properties:
                      data:
                        $ref: '#/components/schemas/User'
        '401':
          description: Unauthorized
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

    patch:
      tags:
        - Users
      summary: Update current user profile
      description: Update the profile of the authenticated user
      security:
        - BearerAuth: []
      requestBody:
        required: true
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/UserUpdate'
      responses:
        '200':
          description: Profile updated successfully
          content:
            application/json:
              schema:
                allOf:
                  - $ref: '#/components/schemas/ApiResponse'
                  - type: object
                    properties:
                      data:
                        $ref: '#/components/schemas/User'
        '400':
          description: Validation error
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '401':
          description: Unauthorized
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

  # Organization endpoints
  /organizations:
    get:
      tags:
        - Organizations
      summary: List user organizations
      description: Retrieve organizations the user belongs to
      security:
        - BearerAuth: []
      parameters:
        - name: page
          in: query
          schema:
            type: integer
            minimum: 1
            default: 1
        - name: limit
          in: query
          schema:
            type: integer
            minimum: 1
            maximum: 100
            default: 20
        - name: sort
          in: query
          schema:
            type: string
            enum: [name, createdAt, updatedAt]
            default: createdAt
        - name: order
          in: query
          schema:
            type: string
            enum: [asc, desc]
            default: desc
      responses:
        '200':
          description: Organizations retrieved successfully
          content:
            application/json:
              schema:
                allOf:
                  - $ref: '#/components/schemas/ApiResponse'
                  - type: object
                    properties:
                      data:
                        type: array
                        items:
                          $ref: '#/components/schemas/Organization'
        '401':
          description: Unauthorized
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

    post:
      tags:
        - Organizations
      summary: Create organization
      description: Create a new organization
      security:
        - BearerAuth: []
      requestBody:
        required: true
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/OrganizationCreate'
      responses:
        '201':
          description: Organization created successfully
          content:
            application/json:
              schema:
                allOf:
                  - $ref: '#/components/schemas/ApiResponse'
                  - type: object
                    properties:
                      data:
                        $ref: '#/components/schemas/Organization'
        '400':
          description: Validation error
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '401':
          description: Unauthorized
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '409':
          description: Organization slug already exists
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

  /organizations/{orgId}:
    get:
      tags:
        - Organizations
      summary: Get organization details
      description: Retrieve details of a specific organization
      security:
        - BearerAuth: []
        - OrganizationAuth: []
      parameters:
        - name: orgId
          in: path
          required: true
          schema:
            $ref: '#/components/schemas/UUID'
      responses:
        '200':
          description: Organization retrieved successfully
          content:
            application/json:
              schema:
                allOf:
                  - $ref: '#/components/schemas/ApiResponse'
                  - type: object
                    properties:
                      data:
                        $ref: '#/components/schemas/Organization'
        '401':
          description: Unauthorized
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '403':
          description: Access denied
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '404':
          description: Organization not found
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

    patch:
      tags:
        - Organizations
      summary: Update organization
      description: Update organization details
      security:
        - BearerAuth: []
        - OrganizationAuth: []
      parameters:
        - name: orgId
          in: path
          required: true
          schema:
            $ref: '#/components/schemas/UUID'
      requestBody:
        required: true
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/OrganizationUpdate'
      responses:
        '200':
          description: Organization updated successfully
          content:
            application/json:
              schema:
                allOf:
                  - $ref: '#/components/schemas/ApiResponse'
                  - type: object
                    properties:
                      data:
                        $ref: '#/components/schemas/Organization'
        '400':
          description: Validation error
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '401':
          description: Unauthorized
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '403':
          description: Access denied
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '404':
          description: Organization not found
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

    delete:
      tags:
        - Organizations
      summary: Delete organization
      description: Delete an organization (owner only)
      security:
        - BearerAuth: []
        - OrganizationAuth: []
      parameters:
        - name: orgId
          in: path
          required: true
          schema:
            $ref: '#/components/schemas/UUID'
      responses:
        '204':
          description: Organization deleted successfully
        '401':
          description: Unauthorized
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '403':
          description: Access denied
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '404':
          description: Organization not found
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

  # Member endpoints
  /organizations/{orgId}/members:
    get:
      tags:
        - Members
      summary: List organization members
      description: Retrieve all members of an organization
      security:
        - BearerAuth: []
        - OrganizationAuth: []
      parameters:
        - name: orgId
          in: path
          required: true
          schema:
            $ref: '#/components/schemas/UUID'
        - name: page
          in: query
          schema:
            type: integer
            minimum: 1
            default: 1
        - name: limit
          in: query
          schema:
            type: integer
            minimum: 1
            maximum: 100
            default: 20
        - name: status
          in: query
          schema:
            type: string
            enum: [active, suspended, pending]
      responses:
        '200':
          description: Members retrieved successfully
          content:
            application/json:
              schema:
                allOf:
                  - $ref: '#/components/schemas/ApiResponse'
                  - type: object
                    properties:
                      data:
                        type: array
                        items:
                          $ref: '#/components/schemas/Member'
        '401':
          description: Unauthorized
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '403':
          description: Access denied
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '404':
          description: Organization not found
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

    post:
      tags:
        - Members
      summary: Add member to organization
      description: Add a new member to an organization
      security:
        - BearerAuth: []
        - OrganizationAuth: []
      parameters:
        - name: orgId
          in: path
          required: true
          schema:
            $ref: '#/components/schemas/UUID'
      requestBody:
        required: true
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/MemberCreate'
      responses:
        '201':
          description: Member added successfully
          content:
            application/json:
              schema:
                allOf:
                  - $ref: '#/components/schemas/ApiResponse'
                  - type: object
                    properties:
                      data:
                        $ref: '#/components/schemas/Member'
        '400':
          description: Validation error
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '401':
          description: Unauthorized
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '403':
          description: Access denied
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '404':
          description: Organization not found
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '409':
          description: User is already a member
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

  /members/{memberId}:
    patch:
      tags:
        - Members
      summary: Update member
      description: Update member roles or status
      security:
        - BearerAuth: []
        - OrganizationAuth: []
      parameters:
        - name: memberId
          in: path
          required: true
          schema:
            $ref: '#/components/schemas/UUID'
      requestBody:
        required: true
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/MemberUpdate'
      responses:
        '200':
          description: Member updated successfully
          content:
            application/json:
              schema:
                allOf:
                  - $ref: '#/components/schemas/ApiResponse'
                  - type: object
                    properties:
                      data:
                        $ref: '#/components/schemas/Member'
        '400':
          description: Validation error
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '401':
          description: Unauthorized
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '403':
          description: Access denied
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '404':
          description: Member not found
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

    delete:
      tags:
        - Members
      summary: Remove member
      description: Remove a member from an organization
      security:
        - BearerAuth: []
        - OrganizationAuth: []
      parameters:
        - name: memberId
          in: path
          required: true
          schema:
            $ref: '#/components/schemas/UUID'
      responses:
        '204':
          description: Member removed successfully
        '401':
          description: Unauthorized
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '403':
          description: Access denied
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '404':
          description: Member not found
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

  # Invitation endpoints
  /organizations/{orgId}/invitations:
    get:
      tags:
        - Invitations
      summary: List organization invitations
      description: Retrieve all invitations for an organization
      security:
        - BearerAuth: []
        - OrganizationAuth: []
      parameters:
        - name: orgId
          in: path
          required: true
          schema:
            $ref: '#/components/schemas/UUID'
        - name: status
          in: query
          schema:
            type: string
            enum: [pending, accepted, expired, revoked]
      responses:
        '200':
          description: Invitations retrieved successfully
          content:
            application/json:
              schema:
                allOf:
                  - $ref: '#/components/schemas/ApiResponse'
                  - type: object
                    properties:
                      data:
                        type: array
                        items:
                          $ref: '#/components/schemas/Invitation'
        '401':
          description: Unauthorized
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '403':
          description: Access denied
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '404':
          description: Organization not found
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

    post:
      tags:
        - Invitations
      summary: Create invitation
      description: Send an invitation to join an organization
      security:
        - BearerAuth: []
        - OrganizationAuth: []
      parameters:
        - name: orgId
          in: path
          required: true
          schema:
            $ref: '#/components/schemas/UUID'
      requestBody:
        required: true
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/InvitationCreate'
      responses:
        '201':
          description: Invitation created successfully
          content:
            application/json:
              schema:
                allOf:
                  - $ref: '#/components/schemas/ApiResponse'
                  - type: object
                    properties:
                      data:
                        $ref: '#/components/schemas/Invitation'
        '400':
          description: Validation error
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '401':
          description: Unauthorized
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '403':
          description: Access denied
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '404':
          description: Organization not found
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '409':
          description: Invitation already exists
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

  /invitations/{invitationId}/resend:
    post:
      tags:
        - Invitations
      summary: Resend invitation
      description: Resend an existing invitation
      security:
        - BearerAuth: []
        - OrganizationAuth: []
      parameters:
        - name: invitationId
          in: path
          required: true
          schema:
            $ref: '#/components/schemas/UUID'
      responses:
        '200':
          description: Invitation resent successfully
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '401':
          description: Unauthorized
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '403':
          description: Access denied
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '404':
          description: Invitation not found
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

  /invitations/{invitationId}:
    delete:
      tags:
        - Invitations
      summary: Revoke invitation
      description: Revoke a pending invitation
      security:
        - BearerAuth: []
        - OrganizationAuth: []
      parameters:
        - name: invitationId
          in: path
          required: true
          schema:
            $ref: '#/components/schemas/UUID'
      responses:
        '200':
          description: Invitation revoked successfully
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '401':
          description: Unauthorized
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '403':
          description: Access denied
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '404':
          description: Invitation not found
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

  /invitations/accept:
    post:
      tags:
        - Invitations
      summary: Accept invitation
      description: Accept an invitation to join an organization
      security:
        - BearerAuth: []
      requestBody:
        required: true
        content:
          application/json:
            schema:
              type: object
              required:
                - token
              properties:
                token:
                  type: string
                  example: "inv_abc123def456"
      responses:
        '200':
          description: Invitation accepted successfully
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '400':
          description: Invalid or expired token
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '401':
          description: Authentication required
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

  # Resource quota endpoints
  /organizations/{orgId}/quotas:
    get:
      tags:
        - Resources
      summary: Get organization resource quotas
      description: Retrieve current resource quotas and usage for an organization
      security:
        - BearerAuth: []
        - OrganizationAuth: []
      parameters:
        - name: orgId
          in: path
          required: true
          schema:
            $ref: '#/components/schemas/UUID'
      responses:
        '200':
          description: Quotas retrieved successfully
          content:
            application/json:
              schema:
                allOf:
                  - $ref: '#/components/schemas/ApiResponse'
                  - type: object
                    properties:
                      data:
                        type: array
                        items:
                          $ref: '#/components/schemas/ResourceQuota'
        '401':
          description: Unauthorized
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '403':
          description: Access denied
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '404':
          description: Organization not found
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

  /organizations/{orgId}/quotas/check:
    post:
      tags:
        - Resources
      summary: Check resource quota
      description: Check if a resource consumption request is within quota limits
      security:
        - BearerAuth: []
        - OrganizationAuth: []
      parameters:
        - name: orgId
          in: path
          required: true
          schema:
            $ref: '#/components/schemas/UUID'
      requestBody:
        required: true
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/QuotaCheck'
      responses:
        '200':
          description: Quota check completed
          content:
            application/json:
              schema:
                allOf:
                  - $ref: '#/components/schemas/ApiResponse'
                  - type: object
                    properties:
                      data:
                        $ref: '#/components/schemas/QuotaCheckResponse'
        '400':
          description: Validation error
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '401':
          description: Unauthorized
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '403':
          description: Access denied
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '404':
          description: Organization not found
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

  # Analytics endpoints
  /organizations/{orgId}/analytics/usage-report:
    get:
      tags:
        - Analytics
      summary: Generate usage report
      description: Generate a comprehensive usage report for an organization
      security:
        - BearerAuth: []
        - OrganizationAuth: []
      parameters:
        - name: orgId
          in: path
          required: true
          schema:
            $ref: '#/components/schemas/UUID'
        - name: startDate
          in: query
          schema:
            type: string
            format: date
            description: Start date for the report (default: 30 days ago)
        - name: endDate
          in: query
          schema:
            type: string
            format: date
            description: End date for the report (default: today)
      responses:
        '200':
          description: Usage report generated successfully
          content:
            application/json:
              schema:
                allOf:
                  - $ref: '#/components/schemas/ApiResponse'
                  - type: object
                    properties:
                      data:
                        $ref: '#/components/schemas/UsageReport'
        '401':
          description: Unauthorized
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '403':
          description: Access denied
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '404':
          description: Organization not found
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

  /organizations/{orgId}/analytics/top-consumers:
    get:
      tags:
        - Analytics
      summary: Get top resource consumers
      description: Retrieve top consumers of a specific resource type
      security:
        - BearerAuth: []
        - OrganizationAuth: []
      parameters:
        - name: orgId
          in: path
          required: true
          schema:
            $ref: '#/components/schemas/UUID'
        - name: resourceType
          in: query
          required: true
          schema:
            type: string
            enum: [api_requests, storage, compute_time]
        - name: limit
          in: query
          schema:
            type: integer
            minimum: 1
            maximum: 100
            default: 10
      responses:
        '200':
          description: Top consumers retrieved successfully
          content:
            application/json:
              schema:
                allOf:
                  - $ref: '#/components/schemas/ApiResponse'
                  - type: object
                    properties:
                      data:
                        type: array
                        items:
                          $ref: '#/components/schemas/TopConsumer'
        '400':
          description: Invalid resource type
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '401':
          description: Unauthorized
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '403':
          description: Access denied
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '404':
          description: Organization not found
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

  /organizations/{orgId}/analytics/trends:
    get:
      tags:
        - Analytics
      summary: Get usage trends
      description: Retrieve usage trends for a specific resource type over time
      security:
        - BearerAuth: []
        - OrganizationAuth: []
      parameters:
        - name: orgId
          in: path
          required: true
          schema:
            $ref: '#/components/schemas/UUID'
        - name: resourceType
          in: query
          required: true
          schema:
            type: string
            enum: [api_requests, storage, compute_time]
        - name: days
          in: query
          schema:
            type: integer
            minimum: 1
            maximum: 365
            default: 30
      responses:
        '200':
          description: Usage trends retrieved successfully
          content:
            application/json:
              schema:
                allOf:
                  - $ref: '#/components/schemas/ApiResponse'
                  - type: object
                    properties:
                      data:
                        type: array
                        items:
                          $ref: '#/components/schemas/UsageTrendData'
        '400':
          description: Invalid resource type
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '401':
          description: Unauthorized
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '403':
          description: Access denied
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'
        '404':
          description: Organization not found
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ApiResponse'

# Tags
tags:
  - name: Authentication
    description: User authentication and token management
  - name: Users
    description: User profile management
  - name: Organizations
    description: Organization management
  - name: Members
    description: Organization member management
  - name: Invitations
    description: Organization invitation system
  - name: Resources
    description: Resource quota and usage management
  - name: Analytics
    description: Usage analytics and reporting
````

## Usage Instructions

### 1. Save the Specification

Save the above YAML content as `openapi.yaml` in your project root.

### 2. Generate Documentation

Use tools like Swagger UI, Redoc, or Stoplight to generate interactive documentation:

```bash
# Using Swagger UI Docker
docker run -p 80:8080 -e SWAGGER_JSON=/openapi.yaml -v $(pwd)/openapi.yaml:/openapi.yaml swaggerapi/swagger-ui

# Using Redoc CLI
npm install -g redoc-cli
redoc-cli bundle openapi.yaml --output api-docs.html
```

### 3. Generate Client SDKs

Use OpenAPI Generator to create client libraries:

```bash
# Generate TypeScript client
docker run --rm -v "${PWD}:/local" openapitools/openapi-generator-cli generate \
  -i /local/openapi.yaml \
  -g typescript-axios \
  -o /local/generated/typescript

# Generate Python client
docker run --rm -v "${PWD}:/local" openapitools/openapi-generator-cli generate \
  -i /local/openapi.yaml \
  -g python \
  -o /local/generated/python
```

### 4. API Validation

Use the specification for request/response validation in your API server:

```typescript
import { validateRequest } from './middleware/validation';
import openapiSpec from './openapi.yaml';

// Apply validation middleware
app.use('/v1', validateRequest(openapiSpec));
```

### 5. Testing

Generate test cases from the specification:

```bash
# Using Dredd (API testing)
npm install -g dredd
dredd openapi.yaml http://localhost:3000/v1
```

## Integration with Development Workflow

### 1. Version Control

- Track changes to the OpenAPI specification
- Use semantic versioning for API changes
- Maintain backward compatibility

### 2. CI/CD Integration

- Validate API changes against the specification
- Auto-generate documentation on deployment
- Run contract tests

### 3. Developer Experience

- Interactive API explorer
- Code examples in multiple languages
- SDK generation and distribution

### 4. Monitoring

- Track API usage against specification
- Monitor deprecated endpoint usage
- Alert on breaking changes
