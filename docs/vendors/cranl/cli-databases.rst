Databases
=========

CranL provides managed databases with automatic provisioning, backups, and connection string management.

Supported Types
---------------

.. list-table::
   :header-rows: 1

   * - Type
     - Value
     - Aliases
   * - PostgreSQL
     - ``postgresql``
     - ``pg``, ``postgres``
   * - MySQL
     - ``mysql``
     - —
   * - MariaDB
     - ``mariadb``
     - —
   * - MongoDB
     - ``mongodb``
     - ``mongo``
   * - Redis
     - ``redis``
     - —

cranl db list
-------------

List all databases.

.. code-block:: bash

   cranl db list

Alias: ``cranl db`` (without subcommand)

**Example:**

.. code-block:: text

   $ cranl db list
   Name      Type         Status    Project      ID
   mydb      postgresql   running   Production   db-001
   cache     redis        running   Production   db-002
   analytics mongodb      idle      Staging      db-003

cranl db create
---------------

Create a managed database.

.. code-block:: bash

   cranl db create --name <name> --type <type> [--region REGION] [--inject APP-ID]

**Flags:**

.. list-table::
   :header-rows: 1

   * - Flag
     - Required
     - Description
   * - ``--name <name>``
     - Yes
     - Database name
   * - ``--type <type>``
     - Yes
     - Database type (``postgresql``, ``mysql``, ``mariadb``, ``mongodb``, ``redis``). Aliases: ``pg``, ``postgres``, ``mongo``
   * - ``--region <region>``
     - No
     - Deploy region alias (``eu``, ``us``, ``mena``, ``egypt``, ``asia``). Defaults to ``eu``
   * - ``--inject <app-id>``
     - No
     - Inject ``DATABASE_URL`` into an application

**Region aliases:**

.. list-table::
   :header-rows: 1

   * - Alias
     - Region
   * - ``eu``, ``europe``
     - Germany 1
   * - ``us``, ``usa``
     - US East 1
   * - ``mena``, ``sa``
     - Saudi Arabia 1
   * - ``egypt``, ``eg``
     - Egypt 1
   * - ``asia``, ``india``
     - India 1

**Example:**

.. code-block:: text

   $ cranl db create --name mydb --type pg --region eu --inject a1b2c3d4
   ✓ Database "mydb" (postgresql) created. ID: db-001
   ✓ Injected DATABASE_URL into app a1b2c3d4

cranl db info
-------------

Show database details including connection string.

.. code-block:: bash

   cranl db info <db-id>

**Example:**

.. code-block:: text

   $ cranl db info db-001
     Name:       mydb
     ID:         db-001
     Type:       postgresql
     Status:     running
     Database:   mydb
     User:       admin
     Host:       mydb-abc123.internal
     Connection: postgresql://admin:pass@host:5432/mydb
     Created:    2025-01-15T10:30:00Z

cranl db delete
---------------

Delete a database. Requires the ``--yes`` flag to confirm.

.. code-block:: bash

   cranl db delete <db-id> --yes

.. warning::

   This permanently deletes the database and all its data. This action cannot be undone.

cranl db start
--------------

Start a stopped database.

.. code-block:: bash

   cranl db start <db-id>

cranl db stop
-------------

Stop a running database.

.. code-block:: bash

   cranl db stop <db-id>
