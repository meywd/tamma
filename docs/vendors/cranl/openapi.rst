OpenAPI Specification
====================

The CranL API is described by an OpenAPI 3.0 specification available for download.

Download
--------

:download:`openapi.json <openapi.json>`

You can use this specification with tools like:

- `Swagger UI <https://swagger.io/tools/swagger-ui/>`_
- `Postman <https://www.postman.com/>`_
- `Insomnia <https://insomnia.rest/>`_
- OpenAPI code generators

Base URL
--------

.. code-block:: text

   https://app.cranl.com/api

Authentication
--------------

All endpoints require a Bearer token:

.. code-block:: text

   Authorization: Bearer cranl_sk_...

See :doc:`api/authentication` for details on obtaining API keys.
