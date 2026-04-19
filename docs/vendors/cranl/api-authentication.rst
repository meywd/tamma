Authentication
==============

CranL uses API keys for programmatic access. API keys provide the same access as your user account.

API Key Format
--------------

API keys use the format:

.. code-block:: text

   cranl_sk_<32 random characters>

Example: ``cranl_sk_a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5p6``

Creating API Keys
-----------------

1. Go to `Dashboard Settings <https://app.cranl.com/dashboard/settings>`_
2. Scroll to the **API Keys** section
3. Click **Create API Key**
4. Enter a descriptive name
5. Copy the key — it is shown **only once**

You can have up to **10 active API keys**.

Using API Keys
--------------

Send the API key as a Bearer token in the ``Authorization`` header:

.. code-block:: bash

   curl -X GET \
     -H "Authorization: Bearer cranl_sk_..." \
     -H "Content-Type: application/json" \
     https://app.cranl.com/api/applications

Verify API Key
--------------

.. http:post:: /api/cli/auth/verify

   Verify an API key and return user and organization information.

   **Request Headers:**

   - ``Authorization: Bearer cranl_sk_...``

   **Response:**

   .. code-block:: json

      {
        "user": {
          "id": "550e8400-e29b-41d4-a716-446655440000",
          "email": "alice@example.com",
          "firstName": "Alice",
          "lastName": "Smith"
        },
        "organization": {
          "id": "660e8400-e29b-41d4-a716-446655440001",
          "name": "My Organization"
        }
      }

Revoking API Keys
-----------------

Revoke a key from the dashboard settings page. Revoked keys stop working immediately.

Security Best Practices
-----------------------

- **Never commit API keys** to version control
- **Use environment variables** to store keys in CI/CD
- **Rotate keys regularly** — create a new key, update your systems, then revoke the old one
- **Use descriptive names** so you know which key is used where (e.g., "CI/CD Pipeline", "Local Development")
- Keys are stored as **bcrypt hashes** on the server — a database breach does not expose your keys
