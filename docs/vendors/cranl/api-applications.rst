Applications API
================

Endpoints for managing applications, deployments, environment variables, domains, and lifecycle operations.

List Applications
-----------------

.. http:get:: /api/applications

   List all applications the authenticated user has access to.

   **Response:**

   .. code-block:: json

      [
        {
          "id": "550e8400-e29b-41d4-a716-446655440000",
          "name": "my-api",
          "description": "Backend API",
          "status": "running",
          "branch": "main",
          "project_id": "660e8400-...",
          "project_name": "Production",
          "created_at": "2025-01-15T10:30:00Z"
        }
      ]

   **Status values:** ``running``, ``done``, ``error``, ``idle``, ``pending``

Create Application
------------------

.. http:post:: /api/applications

   Create a new application from a GitHub repository.

   **Request Body:**

   .. code-block:: json

      {
        "name": "my-api",
        "projectId": "660e8400-...",
        "repositoryId": "repo-id",
        "branch": "main",
        "buildType": "nixpacks",
        "serverId": "jAVVJm91DTLB7gzdQvukC",
        "buildPath": "/",
        "description": "My backend API"
      }

   .. list-table::
      :header-rows: 1

      * - Field
        - Required
        - Description
      * - ``name``
        - Yes
        - Application name
      * - ``projectId``
        - Yes
        - Project ID
      * - ``repositoryId``
        - Yes
        - GitHub repository ID
      * - ``branch``
        - No
        - Git branch (default: ``main``)
      * - ``buildType``
        - No
        - ``nixpacks`` or ``dockerfile`` (default: ``nixpacks``)
      * - ``serverId``
        - No
        - Deploy region server ID (see :doc:`/cli/regions`)
      * - ``buildPath``
        - No
        - Path to build from (default: ``/``)
      * - ``description``
        - No
        - Application description

   **Response:**

   .. code-block:: json

      {
        "id": "550e8400-...",
        "name": "my-api",
        "status": "pending"
      }

Get Application
---------------

.. http:get:: /api/applications/(id)

   Get details for a specific application.

   :param id: Application ID

   **Response:**

   .. code-block:: json

      {
        "id": "550e8400-...",
        "name": "my-api",
        "description": "Backend API",
        "status": "running",
        "branch": "main",
        "project_id": "660e8400-...",
        "cranl_back_application_id": "internal-id",
        "created_at": "2025-01-15T10:30:00Z"
      }

Delete Application
------------------

.. http:delete:: /api/applications/(id)

   Delete an application. Removes the app, its DNS records, and CDN configuration.

   :param id: Application ID

   **Permissions:** Admin or owner role required.

   **Response:**

   .. code-block:: json

      {
        "success": true
      }

Deploy Application
------------------

.. http:post:: /api/applications/(id)/deploy

   Trigger a new deployment from the configured branch.

   :param id: Application ID

   **Permissions:** Admin or owner role required.

   **Response:**

   .. code-block:: json

      {
        "id": "550e8400-...",
        "status": "deploying"
      }

Lifecycle
---------

.. http:post:: /api/applications/(id)/lifecycle

   Perform a lifecycle action on an application.

   :param id: Application ID

   **Request Body:**

   .. code-block:: json

      {
        "action": "start"
      }

   **Actions:**

   .. list-table::
      :header-rows: 1

      * - Action
        - Description
      * - ``start``
        - Start a stopped application
      * - ``stop``
        - Stop a running application
      * - ``reload``
        - Soft restart
      * - ``rebuild``
        - Full rebuild from source

   **Permissions:** Admin or owner role required. Fails if the organization subscription is suspended.

   **Response:**

   .. code-block:: json

      {
        "success": true,
        "action": "start"
      }

Environment Variables
---------------------

.. http:get:: /api/applications/(id)/environment

   Get environment variables for an application.

   :param id: Application ID

   **Response:**

   .. code-block:: json

      {
        "env": "DATABASE_URL=postgresql://...\nNODE_ENV=production\nPORT=3000"
      }

   Environment variables are returned as a newline-separated string of ``KEY=VALUE`` pairs.

.. http:put:: /api/applications/(id)/environment

   Update environment variables. Replaces all variables with the provided set.

   :param id: Application ID

   **Request Body:**

   .. code-block:: json

      {
        "env": "DATABASE_URL=postgresql://...\nNODE_ENV=production\nPORT=3000"
      }

   **Response:**

   .. code-block:: json

      {
        "success": true
      }

Deployments
-----------

.. http:get:: /api/applications/(id)/deployments

   List deployment history for an application.

   :param id: Application ID

   **Response:**

   .. code-block:: json

      {
        "deployments": [
          {
            "deploymentId": "dep-001",
            "title": "abc1234",
            "description": "fix: update config",
            "status": "done",
            "createdAt": "2025-01-15T10:30:00Z",
            "startedAt": "2025-01-15T10:30:05Z",
            "finishedAt": "2025-01-15T10:32:00Z"
          }
        ]
      }

   **Deployment status values:** ``done``, ``error``, ``running``, ``queued``

.. http:get:: /api/applications/(id)/deployments/(deploymentId)/logs

   Get build logs for a specific deployment.

   :param id: Application ID
   :param deploymentId: Deployment ID

   **Response (completed deployment):**

   .. code-block:: json

      {
        "lines": [
          "[2025-01-15 10:30:05] Building...",
          "[2025-01-15 10:31:00] Build complete",
          "[2025-01-15 10:31:30] Deploying..."
        ]
      }

   For in-progress deployments, the response is a **Server-Sent Events (SSE)** stream.

AI Fix
------

.. http:get:: /api/applications/(id)/deployments/(deploymentId)/ai-fix

   Get AI-generated fix suggestions for a failed deployment.

   :param id: Application ID
   :param deploymentId: Deployment ID (must have status ``error``)

   **Restrictions:** Only works for git-based applications with failed deployments.

   **Response:**

   .. code-block:: json

      {
        "status": "errors_found",
        "app_name": "my-api",
        "error_summary": "Build failed: missing dependency",
        "root_cause": "Package 'xyz' is listed in imports but not in package.json",
        "suggested_fixes": [
          {
            "file_path": "package.json",
            "action": "modify",
            "description": "Add missing dependency",
            "search_replace": [
              {
                "search": "\"dependencies\": {",
                "replace": "\"dependencies\": {\n    \"xyz\": \"^1.0.0\","
              }
            ]
          }
        ],
        "ai_explanation": "The build failed because..."
      }

Domains
-------

.. http:get:: /api/applications/(id)/domains

   List all domains configured for an application.

   :param id: Application ID

   **Response:**

   .. code-block:: json

      {
        "domains": [
          {
            "domainId": "dom-001",
            "host": "my-api-abc123.cranl.net",
            "https": true,
            "certificateType": "wildcard",
            "sslStatus": "active"
          },
          {
            "domainId": "dom-002",
            "host": "api.example.com",
            "https": true,
            "certificateType": "free",
            "sslStatus": "active"
          }
        ],
        "defaultDomain": "my-api-abc123.cranl.net"
      }

.. http:post:: /api/applications/(id)/domains/custom

   Add a custom domain to an application.

   :param id: Application ID

   **Permissions:** Admin or owner role required.

   **Request Body:**

   .. code-block:: json

      {
        "host": "api.example.com"
      }

   **Response:**

   .. code-block:: json

      {
        "success": true,
        "domain": {
          "domainId": "dom-002",
          "host": "api.example.com",
          "https": true,
          "certificateType": "free"
        },
        "sslStatus": "pending",
        "cnameTarget": "my-api-abc123.cranl.net"
      }

   After adding, point a CNAME record from your domain to the ``cnameTarget``.

.. http:delete:: /api/applications/(id)/domains/custom?domainId=(domainId)

   Remove a custom domain.

   :param id: Application ID
   :query domainId: Domain ID to remove

   **Permissions:** Admin or owner role required.

   **Response:**

   .. code-block:: json

      {
        "success": true
      }

Monitoring
----------

.. http:get:: /api/applications/(id)/monitoring

   Get real-time resource usage metrics.

   :param id: Application ID

   **Response:** Monitoring data including CPU, memory, and disk usage metrics from the deployment server.

Analytics
---------

.. http:get:: /api/applications/(id)/analytics?dateFrom=(dateFrom)&dateTo=(dateTo)&granularity=(granularity)

   Get traffic analytics for an application.

   :param id: Application ID
   :query dateFrom: Start date (ISO 8601, optional)
   :query dateTo: End date (ISO 8601, optional)
   :query granularity: ``hour`` or ``day`` (default: ``day``)

   **Response:**

   .. code-block:: json

      {
        "totalBandwidth": 1048576000,
        "totalRequests": 50000,
        "averageResponseTime": 125,
        "requestsChart": {
          "2025-01-15": 5000,
          "2025-01-16": 4800
        },
        "bandwidthChart": {
          "2025-01-15": 104857600,
          "2025-01-16": 99614720
        },
        "topCountries": [
          {"name": "United States", "count": 15000},
          {"name": "Germany", "count": 8000}
        ],
        "topPaths": [
          {"path": "/api/users", "count": 12000},
          {"path": "/api/products", "count": 8000}
        ],
        "errors": {
          "total3xx": 200,
          "total4xx": 500,
          "total5xx": 10
        }
      }

Purge Cache
-----------

.. http:post:: /api/applications/(id)/purge-cache

   Purge the CDN cache for an application.

   :param id: Application ID

   **Permissions:** Admin or owner role required.

   **Response:**

   .. code-block:: json

      {
        "success": true
      }
