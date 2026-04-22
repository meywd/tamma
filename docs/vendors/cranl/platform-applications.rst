Applications
============
Applications are the core of CranL — each one is a deployed service built from a GitHub repository.

Creating an Application
-----------------------
1. Go to **Applications** in the sidebar
2. Click **New Application**
3. Select a GitHub repository from your synced repos
4. Configure the application:

.. list-table::
   :header-rows: 1
   :widths: 25 75

   * - Field
     - Description
   * - **Name**
     - A name for your app (defaults to the repository name)
   * - **Branch**
     - The Git branch to deploy (defaults to ``main``)
   * - **Build Type**
     - ``Railpack`` (auto-detect) or ``Dockerfile``
   * - **Region**
     - Where to deploy your app (see :doc:`/cli/regions` for available regions)

5. Click **Create** — the app deploys immediately

.. note::
   The total number of applications and databases combined is limited by your plan. Basic allows 3, Pro allows 20, Enterprise is unlimited.

The number of Applications you can create depends on your plan:

- **Basic:** 2 Applications
- **Pro:** 10 Applications
- **Enterprise:** Unlimited Applications

Plan Resources
--------------
Each Application comes with dedicated resources based on your plan:

- **Basic:** 2GB RAM DDR5, 2 vCPU Cores
- **Pro:** 4GB RAM DDR5, 4 vCPU Cores

All CPU cores are sourced from the following processors:

- AMD Ryzen 9 5950X
- AMD Ryzen 9 7950X3D
- AMD Ryzen 9 3900


Application Status
------------------
Each application shows a status indicator:

- **Running** — App is live and serving traffic
- **Done** — Last deployment completed successfully
- **Error** — Last deployment failed
- **Idle** — App is stopped
- **Pending** — Deployment in progress

Application Settings
--------------------
Open an application to access its detail page. From here you can manage:

Port
^^^^
Edit the port your application listens on. This must match the port your app binds to internally.

Default Domain
^^^^^^^^^^^^^^
Every application gets a free ``*.cranl.net`` subdomain with SSL. The URL is shown in the settings and can be copied to your clipboard.

Connected Repository
^^^^^^^^^^^^^^^^^^^^
View which GitHub repository and branch the application is built from.

Configuring a Dockerfile
-------------------------
If you select ``Dockerfile`` as the **Build Type**, CranL will use the ``Dockerfile`` found in the root of your repository to build and deploy your application.

Your ``Dockerfile`` must follow these requirements to work correctly on CranL:

- It must be named exactly ``Dockerfile`` (case-sensitive) and placed in the **root** of your repository.
- Your application must bind to the port you configured in the **Port** setting.
- The final image must have a defined ``CMD`` or ``ENTRYPOINT`` instruction to start the application.

Basic Dockerfile Structure
^^^^^^^^^^^^^^^^^^^^^^^^^^
A minimal working ``Dockerfile`` looks like this:

.. code-block:: dockerfile

   # 1. Choose a base image
   FROM node:20-alpine

   # 2. Set the working directory inside the container
   WORKDIR /app

   # 3. Copy dependency files first (for better layer caching)
   COPY package*.json ./

   # 4. Install dependencies
   RUN npm install --production

   # 5. Copy the rest of your application code
   COPY . .

   # 6. Expose the port your app listens on (must match Port setting)
   EXPOSE 3000

   # 7. Define the command to start the application
   CMD ["node", "server.js"]

Common Examples
^^^^^^^^^^^^^^^

**Python (Flask / FastAPI)**

.. code-block:: dockerfile

   FROM python:3.11-slim
   WORKDIR /app
   COPY requirements.txt ./
   RUN pip install --no-cache-dir -r requirements.txt
   COPY . .
   EXPOSE 8000
   CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000"]

**Go**

.. code-block:: dockerfile

   FROM golang:1.22-alpine AS builder
   WORKDIR /app
   COPY . .
   RUN go build -o main .

   FROM alpine:latest
   WORKDIR /app
   COPY --from=builder /app/main .
   EXPOSE 8080
   CMD ["./main"]

**PHP (Laravel / Symfony)**

.. code-block:: dockerfile

   FROM php:8.2-fpm-alpine
   WORKDIR /var/www
   COPY . .
   RUN docker-php-ext-install pdo pdo_mysql
   EXPOSE 9000
   CMD ["php-fpm"]

Best Practices
^^^^^^^^^^^^^^
- Use a **slim or alpine** base image to reduce image size and build time.
- Copy dependency files (``package.json``, ``requirements.txt``, etc.) **before** copying the rest of your code to take advantage of Docker layer caching.
- Always bind your application to ``0.0.0.0`` and not ``localhost`` or ``127.0.0.1``, otherwise CranL cannot route traffic to it.
- Avoid storing secrets in the ``Dockerfile``. Use :doc:`environment-variables` instead.
- Use a **multi-stage build** (as shown in the Go example) for compiled languages to keep the final image small.

.. warning::
   If your application binds to ``localhost`` or ``127.0.0.1`` instead of ``0.0.0.0``, it will not be reachable and will show an **Error** status after deployment.

Deleting an Application
-----------------------
1. Open the application detail page
2. Scroll to the bottom
3. Click **Delete Application**
4. Confirm the deletion

.. warning::
   Deleting an application removes it permanently along with its DNS records and CDN configuration. This cannot be undone.

See Also
--------
- :doc:`deployments` — Deploy and view build logs
- :doc:`environment-variables` — Set environment variables
- :doc:`domains-ssl` — Add custom domains
- :doc:`monitoring` — Resource usage metrics
