Applications
============

Commands for creating, deploying, and managing applications.

cranl apps list
---------------

List all applications you have access to.

.. code-block:: bash

   cranl apps list

Alias: ``cranl apps`` (without subcommand)

**Example:**

.. code-block:: text

   $ cranl apps list
   Name        Status    Branch  Project      ID
   my-api      running   main    Production   a1b2c3d4
   frontend    error     main    Production   e5f6g7h8
   staging-app idle      dev     Staging      i9j0k1l2

Status is color-coded: **green** (running/done), **red** (error), **yellow** (idle).

cranl apps create
-----------------

Create a new application from a GitHub repository.

.. code-block:: bash

   cranl apps create --repo <repository-id> [--name NAME] [--branch BRANCH] [--build-type TYPE] [--region REGION]

**Prerequisites:**

- Default project must be set (``cranl projects select <project-id>``)
- GitHub must be connected (``cranl github connect``)

**Flags:**

.. list-table::
   :header-rows: 1

   * - Flag
     - Required
     - Description
   * - ``--repo <id>``
     - Yes
     - GitHub repository ID (from ``cranl github repos``)
   * - ``--name <name>``
     - No
     - Application name (defaults to repo name)
   * - ``--branch <branch>``
     - No
     - Git branch to deploy (defaults to ``main``)
   * - ``--build-type <type>``
     - No
     - ``nixpacks`` or ``dockerfile`` (defaults to ``nixpacks``)
   * - ``--region <region>``
     - No
     - Deploy region (see :doc:`regions`, defaults to ``germany-1``)

The application deploys automatically after creation.

**Example:**

.. code-block:: text

   $ cranl apps create --repo 12345 --name my-api --region us-east-1
   ✓ Application "my-api" created (a1b2c3d4-...)

cranl apps info
---------------

Show details for an application.

.. code-block:: bash

   cranl apps info <app-id>

**Example:**

.. code-block:: text

   $ cranl apps info a1b2c3d4
     Name:     my-api
     ID:       a1b2c3d4-e5f6-7890-abcd-ef1234567890
     Status:   running
     Branch:   main
     URL:      https://my-api-abc123.cranl.net
     Created:  2025-01-15T10:30:00Z

cranl apps delete
-----------------

Delete an application. Requires the ``--yes`` flag to confirm.

.. code-block:: bash

   cranl apps delete <app-id> --yes

**Example:**

.. code-block:: text

   $ cranl apps delete a1b2c3d4 --yes
   ✓ Application deleted.

cranl apps deploy
-----------------

Trigger a new deployment.

.. code-block:: bash

   cranl apps deploy <app-id>

**Example:**

.. code-block:: text

   $ cranl apps deploy a1b2c3d4
   ✓ Deployment triggered.
   View logs: cranl apps deployments logs a1b2c3d4 <deployment-id>

cranl apps logs
---------------

View runtime logs for an application.

.. code-block:: bash

   cranl apps logs <app-id>

cranl apps monitoring
---------------------

View CPU, memory, and disk usage.

.. code-block:: bash

   cranl apps monitoring <app-id>

**Example:**

.. code-block:: text

   $ cranl apps monitoring a1b2c3d4
     CPU:    12.5%
     Memory: 256.0 / 512.0 MB
     Disk:   128.0 / 1024.0 MB

Lifecycle Commands
------------------

cranl apps start
^^^^^^^^^^^^^^^^

Start a stopped application.

.. code-block:: bash

   cranl apps start <app-id>

cranl apps stop
^^^^^^^^^^^^^^^

Stop a running application.

.. code-block:: bash

   cranl apps stop <app-id>

cranl apps restart
^^^^^^^^^^^^^^^^^^

Restart an application (soft reload).

.. code-block:: bash

   cranl apps restart <app-id>

cranl apps rebuild
^^^^^^^^^^^^^^^^^^

Rebuild an application from source.

.. code-block:: bash

   cranl apps rebuild <app-id>

Environment Variables
---------------------

See :doc:`/cli/applications` subsection or use these commands directly:

cranl apps env list
^^^^^^^^^^^^^^^^^^^

List environment variables.

.. code-block:: bash

   cranl apps env list <app-id>

**Example:**

.. code-block:: text

   $ cranl apps env list a1b2c3d4
   Key             Value
   DATABASE_URL    postgresql://admin:pass@host:5432/mydb
   NODE_ENV        production
   PORT            3000

cranl apps env set
^^^^^^^^^^^^^^^^^^

Set one or more environment variables. Merges with existing variables.

.. code-block:: bash

   cranl apps env set <app-id> KEY=VALUE [KEY2=VALUE2 ...]

**Example:**

.. code-block:: text

   $ cranl apps env set a1b2c3d4 NODE_ENV=production PORT=3000
   ✓ Updated 2 environment variable(s).

cranl apps env unset
^^^^^^^^^^^^^^^^^^^^

Remove one or more environment variables.

.. code-block:: bash

   cranl apps env unset <app-id> KEY [KEY2 ...]

**Example:**

.. code-block:: text

   $ cranl apps env unset a1b2c3d4 DEBUG
   ✓ Removed 1 environment variable(s).

cranl apps env push
^^^^^^^^^^^^^^^^^^^

Upload a ``.env`` file to an application. Merges with existing variables.

.. code-block:: bash

   cranl apps env push <app-id> [file]

**Arguments:**

.. list-table::
   :header-rows: 1

   * - Argument
     - Required
     - Description
   * - ``app-id``
     - Yes
     - Application ID
   * - ``file``
     - No
     - Path to env file (defaults to ``.env``)

**Example:**

.. code-block:: text

   $ cranl apps env push a1b2c3d4
   ✓ Pushed 8 variable(s) from .env.

Deployment History
------------------

cranl apps deployments list
^^^^^^^^^^^^^^^^^^^^^^^^^^^

View deployment history.

.. code-block:: bash

   cranl apps deployments list <app-id>

**Example:**

.. code-block:: text

   $ cranl apps deployments list a1b2c3d4
   Status  Commit   Message                   Date                  ID
   done    abc1234  fix: update config         2025-01-15 10:30:00   dep-001
   error   def5678  feat: add new endpoint     2025-01-14 09:00:00   dep-002
   done    ghi9012  initial commit             2025-01-13 08:00:00   dep-003

cranl apps deployments logs
^^^^^^^^^^^^^^^^^^^^^^^^^^^^

View build logs for a specific deployment.

.. code-block:: bash

   cranl apps deployments logs <app-id> <deployment-id>
