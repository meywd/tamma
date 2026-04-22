MCP Tools Reference
===================

The CranL MCP server exposes 16 tools that AI assistants can use to manage your infrastructure.

Projects
--------

cranl_list_projects
^^^^^^^^^^^^^^^^^^^

List all projects the user has access to.

**Parameters:** None

**Returns:** Array of projects with ``id``, ``name``, ``organization_id``, ``created_at``.

Apps
----

cranl_list_apps
^^^^^^^^^^^^^^^

List all applications with name, status, branch, project, and ID.

**Parameters:** None

**Returns:** Array of applications with ``id``, ``name``, ``description``, ``status``, ``branch``, ``project_id``, ``project_name``, ``created_at``.

cranl_create_app
^^^^^^^^^^^^^^^^

Create a new application from a GitHub repository.

**Parameters:**

.. list-table::
   :header-rows: 1

   * - Parameter
     - Type
     - Required
     - Description
   * - ``name``
     - string
     - Yes
     - Application name
   * - ``projectId``
     - string
     - Yes
     - Project ID
   * - ``repositoryId``
     - string
     - Yes
     - GitHub repository ID
   * - ``branch``
     - string
     - No
     - Git branch (default: ``main``)
   * - ``buildType``
     - string
     - No
     - ``nixpacks`` or ``dockerfile`` (default: ``nixpacks``)
   * - ``region``
     - string
     - No
     - Deploy region ID from ``cranl_list_regions`` (e.g. ``germany-1``, ``us-east-1``)

**Returns:** Created application object.

cranl_deploy_app
^^^^^^^^^^^^^^^^

Trigger a new deployment for an application.

**Parameters:**

.. list-table::
   :header-rows: 1

   * - Parameter
     - Type
     - Required
     - Description
   * - ``appId``
     - string
     - Yes
     - Application ID

cranl_app_lifecycle
^^^^^^^^^^^^^^^^^^^

Start, stop, restart, or rebuild an application.

**Parameters:**

.. list-table::
   :header-rows: 1

   * - Parameter
     - Type
     - Required
     - Description
   * - ``appId``
     - string
     - Yes
     - Application ID
   * - ``action``
     - string
     - Yes
     - ``start``, ``stop``, ``restart``, or ``rebuild``

Logs & Monitoring
-----------------

cranl_get_app_logs
^^^^^^^^^^^^^^^^^^

Get runtime logs for an application.

**Parameters:**

.. list-table::
   :header-rows: 1

   * - Parameter
     - Type
     - Required
     - Description
   * - ``appId``
     - string
     - Yes
     - Application ID

**Returns:** Object with ``logs`` field.

cranl_get_deployment_logs
^^^^^^^^^^^^^^^^^^^^^^^^^

Get build logs for a specific deployment.

**Parameters:**

.. list-table::
   :header-rows: 1

   * - Parameter
     - Type
     - Required
     - Description
   * - ``appId``
     - string
     - Yes
     - Application ID
   * - ``deploymentId``
     - string
     - Yes
     - Deployment ID

**Returns:** Object with ``logs`` field.

cranl_get_monitoring
^^^^^^^^^^^^^^^^^^^^

Get CPU, memory, and disk monitoring data for an application.

**Parameters:**

.. list-table::
   :header-rows: 1

   * - Parameter
     - Type
     - Required
     - Description
   * - ``appId``
     - string
     - Yes
     - Application ID

**Returns:** Object with ``cpu``, ``memory``, and ``disk`` usage data.

cranl_get_deployments
^^^^^^^^^^^^^^^^^^^^^

Get deployment history for an application.

**Parameters:**

.. list-table::
   :header-rows: 1

   * - Parameter
     - Type
     - Required
     - Description
   * - ``appId``
     - string
     - Yes
     - Application ID

**Returns:** Array of deployments with ``id``, ``status``, ``commit_message``, ``commit_sha``, ``created_at``.

Environment Variables
---------------------

cranl_get_env
^^^^^^^^^^^^^

Get environment variables for an application.

**Parameters:**

.. list-table::
   :header-rows: 1

   * - Parameter
     - Type
     - Required
     - Description
   * - ``appId``
     - string
     - Yes
     - Application ID

**Returns:** Object with ``env`` field (newline-separated ``KEY=VALUE`` pairs).

cranl_set_env
^^^^^^^^^^^^^

Set environment variables. Merges with existing variables — existing variables not included in the update are preserved.

**Parameters:**

.. list-table::
   :header-rows: 1

   * - Parameter
     - Type
     - Required
     - Description
   * - ``appId``
     - string
     - Yes
     - Application ID
   * - ``variables``
     - object
     - Yes
     - Key-value pairs (e.g. ``{"NODE_ENV": "production", "PORT": "3000"}``)

Databases
---------

cranl_create_database
^^^^^^^^^^^^^^^^^^^^^

Create a managed database.

**Parameters:**

.. list-table::
   :header-rows: 1

   * - Parameter
     - Type
     - Required
     - Description
   * - ``name``
     - string
     - Yes
     - Database name
   * - ``projectId``
     - string
     - Yes
     - Project ID
   * - ``type``
     - string
     - Yes
     - ``postgresql``, ``mysql``, ``mariadb``, ``mongodb``, or ``redis``
   * - ``region``
     - string
     - No
     - Deploy region ID from ``cranl_list_regions`` (e.g. ``germany-1``, ``us-east-1``)

**Returns:** Database object with ``id``, ``name``, ``type``, ``status``.

cranl_list_databases
^^^^^^^^^^^^^^^^^^^^

List all managed databases.

**Parameters:** None

**Returns:** Array of databases with ``id``, ``name``, ``type``, ``status``, ``server_id``, ``project_id``, ``project_name``, ``created_at``.

Regions & Domains
-----------------

cranl_list_regions
^^^^^^^^^^^^^^^^^^

List available deploy regions with server IDs.

**Parameters:** None

**Returns:** Array of regions:

.. code-block:: json

   [
     {
       "id": "germany-1",
       "region": "Europe",
       "server": "Germany 1",
       "country": "Germany",
       "available": true
     },
     {
       "id": "saudi-arabia-1",
       "region": "MENA",
       "server": "Saudi Arabia 1",
       "country": "Saudi Arabia",
       "available": true,
       "note": "Pro/Enterprise plan required"
     }
   ]

Use the ``id`` field when passing a region to ``cranl_create_app`` or ``cranl_create_database``.

cranl_list_domains
^^^^^^^^^^^^^^^^^^

List domains configured for an application.

**Parameters:**

.. list-table::
   :header-rows: 1

   * - Parameter
     - Type
     - Required
     - Description
   * - ``appId``
     - string
     - Yes
     - Application ID

**Returns:** Array of domain objects with ``host``, ``https``, ``port``, ``certificateType``.

AI Fix
------

cranl_get_ai_fix
^^^^^^^^^^^^^^^^

Get AI-generated fix suggestions for a failed deployment.

**Parameters:**

.. list-table::
   :header-rows: 1

   * - Parameter
     - Type
     - Required
     - Description
   * - ``appId``
     - string
     - Yes
     - Application ID
   * - ``deploymentId``
     - string
     - Yes
     - Deployment ID (must be a failed deployment)

**Returns:** Object with ``error_summary``, ``root_cause``, ``suggested_fixes``, and ``ai_explanation``.

MCP Resource
------------

cranl://platform-info
^^^^^^^^^^^^^^^^^^^^^

A read-only resource that provides platform documentation to AI assistants. This helps the AI understand CranL's capabilities without needing to make API calls.

**Content includes:**

- Available database types and their features
- Deploy regions with server IDs
- Build types (Nixpacks vs Dockerfile)
- How environment variables work
- Custom domain setup process
- Connection string injection pattern
- Typical deployment workflow
