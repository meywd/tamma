Authentication
==============

cranl login
-----------

Authenticate with CranL using an API key.

.. code-block:: bash

   cranl login <api-key>

**Flow:**

1. Validates the key format (must start with ``cranl_sk_``)
2. Verifies the key against the CranL API
3. Stores the key in ``~/.cranl/config.json``

**Example:**

.. code-block:: text

   $ cranl login cranl_sk_abc12345...
   ✓ Authenticated as alice@example.com (My Organization)

.. admonition:: Getting an API Key
   :class: tip

   Generate API keys from your `dashboard settings <https://app.cranl.com/dashboard/settings>`_
   under the **API Keys** section. The full key is shown only once at creation time.

cranl logout
------------

Remove stored credentials.

.. code-block:: bash

   cranl logout

Deletes the API key and default project ID from the local config file.

**Example:**

.. code-block:: text

   $ cranl logout
   ✓ Logged out successfully.

cranl whoami
------------

Display current user and organization information.

.. code-block:: bash

   cranl whoami

**Example:**

.. code-block:: text

   $ cranl whoami
     Email:        alice@example.com
     Name:         Alice Smith
     Organization: My Organization
     Org ID:       550e8400-e29b-41d4-a716-446655440000
     Project:      Production
