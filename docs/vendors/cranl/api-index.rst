API Reference
=============

The CranL REST API lets you manage applications, databases, and projects programmatically.

Base URL
--------

.. code-block:: text

   https://app.cranl.com/api

Authentication
--------------

All API requests require authentication via an API key sent as a Bearer token:

.. code-block:: bash

   curl -H "Authorization: Bearer cranl_sk_..." https://app.cranl.com/api/applications

See :doc:`authentication` for details on creating and managing API keys.

Response Format
---------------

All responses are JSON. Successful responses return the requested data directly. Error responses have this shape:

.. code-block:: json

   {
     "error": "Description of the error"
   }

HTTP Status Codes
-----------------

.. list-table::
   :header-rows: 1

   * - Code
     - Description
   * - ``200``
     - Success
   * - ``400``
     - Bad request (invalid parameters)
   * - ``401``
     - Unauthorized (invalid or missing API key)
   * - ``403``
     - Forbidden (insufficient permissions or suspended account)
   * - ``404``
     - Resource not found
   * - ``429``
     - Rate limit exceeded
   * - ``500``
     - Internal server error

Rate Limits
-----------

API key requests are limited to **120 requests per minute**. When exceeded, the API returns ``429 Too Many Requests``.

.. toctree::
   :hidden:

   authentication
   applications
   databases
   projects
