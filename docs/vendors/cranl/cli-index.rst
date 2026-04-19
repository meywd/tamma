CLI Reference
=============

The CranL CLI lets you manage applications, databases, and infrastructure from the terminal.

.. code-block:: text

   cranl <command> [subcommand] [arguments] [flags]

Installation
------------

macOS & Linux
^^^^^^^^^^^^^

.. code-block:: bash

   curl -fsSL https://cranl.com/install.sh | bash

This detects your OS and architecture, downloads the correct binary, verifies the checksum, and installs to ``/usr/local/bin/cranl``.

Windows
^^^^^^^

.. code-block:: powershell

   powershell -NoExit -c "irm https://cranl.com/install.ps1 | iex"

Installs to ``%LOCALAPPDATA%\cranl\cranl.exe`` and adds it to your user PATH.

Manual Download
^^^^^^^^^^^^^^^

Download from ``https://cli.cranl.com/``:

.. list-table::
   :header-rows: 1

   * - Platform
     - Binary
   * - Linux x64
     - ``cranl-linux-x64``
   * - Linux ARM64
     - ``cranl-linux-arm64``
   * - macOS x64 (Intel)
     - ``cranl-darwin-x64``
   * - macOS ARM64 (Apple Silicon)
     - ``cranl-darwin-arm64``
   * - Windows x64
     - ``cranl-windows-x64.exe``

After downloading:

.. code-block:: bash

   chmod +x cranl-linux-x64
   sudo mv cranl-linux-x64 /usr/local/bin/cranl

Verify with ``cranl version``. Update with ``cranl update``. Uninstall by removing the binary and ``~/.cranl``.

Commands Overview
-----------------

.. list-table::
   :header-rows: 1
   :widths: 30 70

   * - Command
     - Description
   * - ``cranl login``
     - Authenticate with an API key
   * - ``cranl logout``
     - Remove stored credentials
   * - ``cranl whoami``
     - Show current user info
   * - ``cranl projects list``
     - List projects
   * - ``cranl projects create``
     - Create a project
   * - ``cranl projects select``
     - Set default project
   * - ``cranl apps list``
     - List applications
   * - ``cranl apps create``
     - Create application from GitHub
   * - ``cranl apps deploy``
     - Trigger deployment
   * - ``cranl apps logs``
     - View runtime logs
   * - ``cranl apps env set``
     - Set environment variables
   * - ``cranl db list``
     - List databases
   * - ``cranl db create``
     - Create managed database
   * - ``cranl regions``
     - List deploy regions
   * - ``cranl mcp``
     - Start MCP server for AI IDEs
   * - ``cranl update``
     - Self-update the CLI
   * - ``cranl version``
     - Print version

Configuration
-------------

The CLI stores configuration in ``~/.cranl/config.json`` with ``0600`` permissions (owner-only read/write).

.. code-block:: json

   {
     "api_key": "cranl_sk_...",
     "api_url": "https://app.cranl.com",
     "default_project_id": "uuid"
   }

Global Behavior
---------------

- All API communication is over **HTTPS** (HTTP is rejected)
- Authentication uses **Bearer token** in the ``Authorization`` header
- The CLI never echoes your API key after initial login
- Commands that require a project use the default project set by ``cranl projects select``

.. toctree::
   :hidden:

   authentication
   projects
   applications
   databases
   domains
   github
   regions
