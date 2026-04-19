Databases
=========

CranL provides managed databases with automatic provisioning and connection string management.

Supported Types
---------------

.. list-table::
   :header-rows: 1

   * - Database
     - Description
   * - **PostgreSQL**
     - Relational database, most popular choice
   * - **MySQL**
     - Widely used relational database
   * - **MariaDB**
     - MySQL-compatible open source database
   * - **MongoDB**
     - Document database for flexible schemas
   * - **Redis**
     - In-memory key-value store for caching and sessions

Creating a Database
-------------------

1. Go to **Applications** in the sidebar
2. Click **New Database**
3. Configure:

   - **Name** — A name for your database
   - **Type** — Select from the supported types above
   - **Region** — Where to deploy (same options as applications)
   - **Inject into App** (optional) — Select an application to automatically add the connection string as an environment variable

4. Click **Create**

Credentials (username, password, connection string) are generated automatically.

.. note::

   Applications and databases share the same plan limit. Basic allows 3 combined, Pro allows 20, Enterprise is unlimited.

Database Status
---------------

- **Running** — Database is online and accepting connections
- **Pending** — Database is being provisioned
- **Stopped** — Database is offline
- **Failed** — Provisioning failed

Managing Databases
------------------

From the database detail page you can:

- **Start** — Bring a stopped database back online
- **Stop** — Take the database offline (data is preserved)
- **Delete** — Permanently remove the database and all its data

.. warning::

   Deleting a database is permanent and cannot be undone. All data stored in the database is lost.

Connection Information
----------------------

The database detail page shows connection details including:

- Database name
- Username
- Host address
- Connection string

Use the connection string in your application's environment variables to connect. If you used the **inject** option during creation, this is done automatically.
