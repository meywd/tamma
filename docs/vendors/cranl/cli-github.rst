GitHub
======

CranL connects to GitHub via the CranL GitHub App. Once connected, you can deploy from any repository the app has access to.

cranl github status
-------------------

Check if GitHub is connected for the current project.

.. code-block:: bash

   cranl github status

**Example:**

.. code-block:: text

   $ cranl github status
   ✓ GitHub is connected. 12 repositories synced.

cranl github connect
--------------------

Open the dashboard in your browser to connect the CranL GitHub App.

.. code-block:: bash

   cranl github connect

This opens ``https://app.cranl.com/dashboard`` where you can install the GitHub App and grant repository access.

cranl github repos
------------------

List synced GitHub repositories. Syncs with GitHub first to pick up any new repos.

.. code-block:: bash

   cranl github repos

**Example:**

.. code-block:: text

   $ cranl github repos
   Repository              Branch  Language    Private
   my-org/api-server       main    TypeScript  No
   my-org/frontend         main    JavaScript  No
   my-org/internal-tool    main    Python      Yes
