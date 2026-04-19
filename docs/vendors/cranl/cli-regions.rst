Regions
=======

CranL deploys to servers across multiple global regions.

cranl regions
-------------

List all available deploy regions.

.. code-block:: bash

   cranl regions

**Example:**

.. code-block:: text

   $ cranl regions
   Region    Server           Location          Status
   Europe    Germany 1        Germany (DE)      Available
   Europe    Turkey 1         Turkey (TR)       Coming Soon
   USA       US East 1        United States (US) Available
   MENA      Saudi Arabia 1   Saudi Arabia (SA)  Available
   MENA      Egypt 1          Egypt (EG)        Available
   MENA      UAE 1            UAE (AE)          Coming Soon
   Asia      India 1          India (IN)        Available
   Asia      Singapore 1      Singapore (SG)    Coming Soon
   Asia      Japan 1          Japan (JP)        Coming Soon

.. note::

   **MENA regions** (Saudi Arabia, Egypt, UAE) require a **Pro** or **Enterprise** plan.

Region Selection
----------------

You select a region when creating an application or database:

.. code-block:: bash

   # Interactive — prompts for region
   cranl apps create

   # Database with region flag
   cranl db create --region eu

**CLI region aliases:**

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
