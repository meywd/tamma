Projects
========

Projects are containers for applications and databases. Every resource belongs to a project.

cranl projects list
-------------------

List all projects you have access to.

.. code-block:: bash

   cranl projects list

Alias: ``cranl projects`` (without subcommand)

**Example:**

.. code-block:: text

   $ cranl projects list
   Name          ID                                    Default
   Production    550e8400-e29b-41d4-a716-446655440000  ✓
   Staging       660e8400-e29b-41d4-a716-446655440001

cranl projects create
---------------------

Create a new project.

.. code-block:: bash

   cranl projects create <name>

**Arguments:**

.. list-table::
   :header-rows: 1

   * - Argument
     - Required
     - Description
   * - ``name``
     - Yes
     - Project name

If this is your first project, it is automatically set as the default.

**Example:**

.. code-block:: text

   $ cranl projects create "Staging"
   ✓ Project "Staging" created (660e8400-e29b-41d4-a716-446655440001)

cranl projects select
---------------------

Set a default project. Many commands (like ``cranl apps create``) require a default project.

.. code-block:: bash

   cranl projects select <project-id>

**Example:**

.. code-block:: text

   $ cranl projects select 660e8400-e29b-41d4-a716-446655440001
   ✓ Default project set to "Staging"

Run ``cranl projects list`` first to find the project ID.
