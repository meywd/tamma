MCP Integration
===============

CranL includes a hosted `Model Context Protocol <https://modelcontextprotocol.io>`_ (MCP) server that lets AI coding assistants manage your infrastructure directly.

Connect your IDE to ``https://app.cranl.com/api/mcp`` with your API key — no binary or local setup needed.

The server exposes 16 tools for deploying apps, creating databases, managing environment variables, viewing logs, and more.

Supported IDEs
--------------

- **Claude Code** (Anthropic)
- **Cursor**
- **Windsurf**
- **VS Code** (with MCP extension)
- Any IDE that supports the MCP protocol

Quick Start
-----------

1. Get an API key from `Settings <https://app.cranl.com/dashboard/settings>`_
2. Add the MCP configuration to your IDE (see :doc:`setup`)
3. Start using CranL tools from your AI assistant

.. tip::

   If you have the CranL CLI installed, run ``cranl mcp`` to see ready-to-copy configuration with your API key pre-filled.

How It Works
------------

Your AI assistant discovers CranL's tools and the ``cranl://platform-info`` resource. It can then deploy apps, create databases, set env vars, check logs, and more — all through natural language.

.. admonition:: Security
   :class: note

   All requests require a valid API key via ``Authorization: Bearer`` header. Connections use HTTPS. Rate limited to 120 requests/minute per key.

.. toctree::
   :hidden:

   setup
   tools
