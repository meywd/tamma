Databases API
=============

Endpoints for creating and managing managed databases.

List Databases
--------------

.. http:get:: /api/databases

   List all databases the authenticated user has access to.

   **Response:**

   .. code-block:: json

      [
        {
          "id": "550e8400-...",
          "name": "mydb",
          "description": "Main database",
          "type": "postgresql",
          "status": "running",
          "server_id": "jAVVJm91DTLB7gzdQvukC",
          "project_id": "660e8400-...",
          "project_name": "Production",
          "created_at": "2025-01-15T10:30:00Z"
        }
      ]

   **Database types:** ``postgresql``, ``mysql``, ``mariadb``, ``mongodb``, ``redis``

Create Database
---------------

.. http:post:: /api/databases

   Create a new managed database.

   **Request Body:**

   .. code-block:: json

      {
        "name": "mydb",
        "projectId": "660e8400-...",
        "type": "postgresql",
        "serverId": "jAVVJm91DTLB7gzdQvukC",
        "description": "Main database"
      }

   .. list-table::
      :header-rows: 1

      * - Field
        - Required
        - Description
      * - ``name``
        - Yes
        - Database name
      * - ``projectId``
        - Yes
        - Project ID
      * - ``type``
        - Yes
        - ``postgresql``, ``mysql``, ``mariadb``, ``mongodb``, or ``redis``
      * - ``serverId``
        - No
        - Deploy region server ID
      * - ``description``
        - No
        - Description

   **Response:**

   .. code-block:: json

      {
        "id": "550e8400-...",
        "name": "mydb",
        "type": "postgresql",
        "status": "pending"
      }

   Passwords and credentials are generated automatically.

Get Database
------------

.. http:get:: /api/databases/(id)

   Get database details including connection information.

   :param id: Database ID

   **Response:**

   .. code-block:: json

      {
        "id": "550e8400-...",
        "name": "mydb",
        "type": "postgresql",
        "project_id": "660e8400-...",
        "cranl_back_database_id": "internal-id"
      }

Update Database
---------------

.. http:patch:: /api/databases/(id)

   Update database name or description.

   :param id: Database ID

   **Request Body:**

   .. code-block:: json

      {
        "name": "new-name",
        "description": "Updated description"
      }

   Both fields are optional.

   **Response:**

   .. code-block:: json

      {
        "success": true
      }

Delete Database
---------------

.. http:delete:: /api/databases/(id)

   Delete a database and all its data.

   :param id: Database ID

   **Response:**

   .. code-block:: json

      {
        "success": true
      }

   .. warning::

      This permanently deletes the database and all data. This action cannot be undone.

Database Lifecycle
------------------

.. http:post:: /api/databases/(id)/(action)

   Perform a lifecycle action on a database.

   :param id: Database ID
   :param action: One of ``start``, ``stop``, ``reload``, ``rebuild``, ``deploy``

   **Response:**

   .. code-block:: json

      {
        "success": true,
        "action": "start",
        "status": "running"
      }

   Fails with ``403`` if the organization subscription is suspended.
