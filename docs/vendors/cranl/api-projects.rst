Projects API
============

Endpoints for managing projects and project members.

List Projects
-------------

.. http:get:: /api/projects

   List all projects the authenticated user has access to.

   **Response:**

   .. code-block:: json

      [
        {
          "id": "550e8400-...",
          "name": "Production",
          "organization_id": "660e8400-...",
          "created_at": "2025-01-15T10:30:00Z",
          "access_type": "organization"
        }
      ]

   **Access types:**

   - ``organization`` — Access via organization membership
   - ``project`` — Access via direct project invitation

Create Project
--------------

.. http:post:: /api/projects

   Create a new project.

   **Request Body:**

   .. code-block:: json

      {
        "name": "Staging",
        "organizationId": "660e8400-..."
      }

   **Response:**

   .. code-block:: json

      {
        "id": "770e8400-...",
        "name": "Staging",
        "organization_id": "660e8400-..."
      }

   Subject to plan limits on number of projects.

Get Project
-----------

.. http:get:: /api/projects/(id)

   Get project details.

   :param id: Project ID

   **Response:**

   .. code-block:: json

      {
        "id": "550e8400-...",
        "name": "Production",
        "organization_id": "660e8400-...",
        "created_by": "user-id",
        "created_at": "2025-01-15T10:30:00Z",
        "app_count": 5,
        "is_owner": true,
        "access_type": "organization"
      }

Update Project
--------------

.. http:put:: /api/projects/(id)

   Update project name.

   :param id: Project ID

   **Permissions:** Project creator or organization owner only.

   **Request Body:**

   .. code-block:: json

      {
        "name": "New Name"
      }

   **Response:**

   .. code-block:: json

      {
        "success": true,
        "name": "New Name"
      }

Delete Project
--------------

.. http:delete:: /api/projects/(id)

   Delete a project. The project must have no applications.

   :param id: Project ID

   **Response:**

   .. code-block:: json

      {
        "success": true
      }

Project Members
---------------

.. http:get:: /api/projects/(id)/members

   List project members and pending invitations.

   :param id: Project ID

   **Response:**

   .. code-block:: json

      {
        "members": [
          {
            "id": "member-001",
            "email": "alice@example.com",
            "role": "admin",
            "status": "active",
            "invited_at": "2025-01-15T10:30:00Z",
            "accepted_at": "2025-01-15T11:00:00Z",
            "first_name": "Alice",
            "last_name": "Smith"
          }
        ],
        "isOwner": true
      }

   **Roles:** ``admin``, ``viewer``

   **Statuses:** ``pending``, ``active``, ``expired``

.. http:post:: /api/projects/(id)/members

   Invite a member to a project.

   :param id: Project ID

   **Permissions:** Project owner or organization owner only.

   **Request Body:**

   .. code-block:: json

      {
        "email": "bob@example.com",
        "role": "viewer"
      }

   **Response:**

   .. code-block:: json

      {
        "success": true,
        "id": "member-002",
        "email": "bob@example.com",
        "role": "viewer",
        "status": "pending",
        "expires_at": "2025-01-16T10:30:00Z"
      }

   Invitations expire after **24 hours**.

.. http:put:: /api/projects/(id)/members/(memberId)

   Update a member's role.

   :param id: Project ID
   :param memberId: Member ID

   **Permissions:** Project owner or organization owner only.

   **Request Body:**

   .. code-block:: json

      {
        "role": "admin"
      }

.. http:delete:: /api/projects/(id)/members/(memberId)

   Remove a member from a project.

   :param id: Project ID
   :param memberId: Member ID

   **Permissions:** Project owner or organization owner only.

   **Response:**

   .. code-block:: json

      {
        "success": true
      }
