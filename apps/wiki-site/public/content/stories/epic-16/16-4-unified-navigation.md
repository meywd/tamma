---
title: "Story 16.4: Cross-Service Unified Navigation Header"
sidebar:
  order: 160
---

Status: ready-for-dev

## Story

As a **platform user**,
I want a consistent navigation header across all Tamma dashboards (app.tamma.dev, elsa.tamma.dev, logs.tamma.dev) showing service links and my logged-in identity,
so that I can seamlessly switch between services and always know where I am and who I am logged in as.

## Acceptance Criteria

1. A shared navigation bar appears at the top of every Tamma dashboard page across all subdomains
2. The navigation bar contains links to: Dashboard (app.tamma.dev), Workflows (elsa.tamma.dev), Logs (logs.tamma.dev)
3. The currently active service is visually highlighted in the navigation bar
4. The navigation bar shows the logged-in user's GitHub avatar and display name (sourced from the oauth2-proxy session headers)
5. An "Admin" link is visible in the navigation bar only when the user has `admin` or `owner` role
6. The "Admin" link navigates to `https://app.tamma.dev/admin`
7. A user menu (dropdown or similar) provides a "Sign Out" option that clears the oauth2-proxy session
8. The navigation bar is injected via nginx `sub_filter` for services that cannot be modified (OpenSearch Dashboards), and as a shared React component for the Tamma Dashboard
9. ELSA Studio (Blazor WASM) receives the navigation bar either via nginx `sub_filter` or a custom Blazor layout component
10. The navigation bar is responsive and works on mobile viewports
11. The navigation bar does not interfere with the host application's own navigation or layout
12. The navigation bar loads asynchronously and does not block the host application's rendering

## Technical Context

### Challenge

Three different applications with different tech stacks need the same navigation:
- **Tamma Dashboard**: React SPA (full control, can add React component)
- **ELSA Studio**: Blazor WASM (limited control, can modify the custom Tamma.Studio project from Story 14.1)
- **OpenSearch Dashboards**: Pre-built application (no source modification possible)

### Approach: Hybrid (nginx injection + native components)

For OpenSearch Dashboards (and any future third-party dashboards), use nginx `sub_filter` to inject a self-contained HTML/CSS/JS snippet into the page. For Tamma Dashboard and ELSA Studio, use native components that share the same visual design.

### Files to Create

| File | Purpose |
|------|---------|
| `docker/nav-header/tamma-nav.html` | Self-contained HTML/CSS/JS navigation bar snippet for nginx injection |
| `docker/nav-header/tamma-nav.css` | Extracted CSS for the navigation bar (optional, can be inline) |
| `docker/nav-header/tamma-nav.js` | JavaScript for user menu, active state detection, role-based visibility |
| `packages/dashboard/src/components/NavHeader.tsx` | React navigation bar component for Tamma Dashboard |

### Files to Modify

| File | Change |
|------|--------|
| `docker/nginx-proxy.conf` | Add `sub_filter` directives for logs.tamma.dev and elsa.tamma.dev to inject the nav header HTML |
| `packages/dashboard/src/App.tsx` (or layout) | Add `<NavHeader />` component to the layout |
| `apps/tamma-elsa/src/Tamma.Studio/` (layout files) | Add navigation bar to the Blazor layout (if custom Studio exists from Story 14.1) |

## Implementation Plan

### Step 1: Self-Contained Navigation Bar (HTML/CSS/JS)

This snippet is injected by nginx into third-party dashboards. It must be self-contained (no external dependencies), styled with scoped CSS (to avoid conflicts), and load user data from the oauth2-proxy headers or a lightweight API call.

```html
<!-- docker/nav-header/tamma-nav.html -->
<div id="tamma-nav-bar" style="display:none">
  <style>
    #tamma-nav-bar {
      position: fixed;
      top: 0;
      left: 0;
      right: 0;
      z-index: 99999;
      height: 48px;
      background: #1a1a2e;
      color: #e0e0e0;
      display: flex !important;
      align-items: center;
      padding: 0 16px;
      font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
      font-size: 14px;
      box-shadow: 0 2px 4px rgba(0,0,0,0.3);
    }
    #tamma-nav-bar a {
      color: #b0b0c0;
      text-decoration: none;
      padding: 8px 12px;
      border-radius: 4px;
      transition: background 0.2s, color 0.2s;
    }
    #tamma-nav-bar a:hover { background: #2a2a4e; color: #fff; }
    #tamma-nav-bar a.active { background: #7c3aed; color: #fff; }
    #tamma-nav-bar .tamma-logo {
      font-weight: 700;
      font-size: 16px;
      color: #7c3aed;
      margin-right: 24px;
    }
    #tamma-nav-bar .tamma-spacer { flex: 1; }
    #tamma-nav-bar .tamma-user {
      display: flex;
      align-items: center;
      gap: 8px;
      cursor: pointer;
      padding: 4px 8px;
      border-radius: 4px;
    }
    #tamma-nav-bar .tamma-user:hover { background: #2a2a4e; }
    #tamma-nav-bar .tamma-avatar {
      width: 28px;
      height: 28px;
      border-radius: 50%;
    }
    #tamma-nav-bar .tamma-user-menu {
      display: none;
      position: absolute;
      right: 16px;
      top: 48px;
      background: #1a1a2e;
      border: 1px solid #2a2a4e;
      border-radius: 4px;
      box-shadow: 0 4px 12px rgba(0,0,0,0.3);
      min-width: 160px;
    }
    #tamma-nav-bar .tamma-user-menu.open { display: block; }
    #tamma-nav-bar .tamma-user-menu a {
      display: block;
      padding: 10px 16px;
      border-radius: 0;
    }
    #tamma-nav-bar .tamma-admin-link { display: none; }
    /* Push page content down */
    body { padding-top: 48px !important; }
  </style>

  <span class="tamma-logo">Tamma</span>

  <a href="https://app.tamma.dev" data-service="app">Dashboard</a>
  <a href="https://elsa.tamma.dev" data-service="elsa">Workflows</a>
  <a href="https://logs.tamma.dev" data-service="logs">Logs</a>
  <a href="https://app.tamma.dev/admin" data-service="admin" class="tamma-admin-link">Admin</a>

  <div class="tamma-spacer"></div>

  <div class="tamma-user" onclick="document.querySelector('.tamma-user-menu').classList.toggle('open')">
    <img class="tamma-avatar" src="" alt="" id="tamma-user-avatar" />
    <span id="tamma-user-name"></span>
  </div>

  <div class="tamma-user-menu">
    <a href="https://app.tamma.dev/settings">Settings</a>
    <a href="https://app.tamma.dev/oauth2/sign_out">Sign Out</a>
  </div>
</div>

<script>
(function() {
  var nav = document.getElementById('tamma-nav-bar');

  // Detect active service from hostname
  var host = window.location.hostname;
  var serviceMap = {
    'app.tamma.dev': 'app',
    'elsa.tamma.dev': 'elsa',
    'logs.tamma.dev': 'logs'
  };
  var activeService = serviceMap[host] || 'app';

  var links = nav.querySelectorAll('a[data-service]');
  for (var i = 0; i < links.length; i++) {
    if (links[i].getAttribute('data-service') === activeService) {
      links[i].classList.add('active');
    }
  }

  // Fetch user info from Tamma API (using the shared tamma_session cookie)
  fetch('https://app.tamma.dev/api/auth/me', { credentials: 'include' })
    .then(function(r) { return r.ok ? r.json() : null; })
    .then(function(data) {
      if (data && data.user) {
        var avatar = document.getElementById('tamma-user-avatar');
        var name = document.getElementById('tamma-user-name');
        avatar.src = 'https://github.com/' + data.user.username + '.png?size=56';
        avatar.alt = data.user.username;
        name.textContent = data.user.username;

        // Show admin link for admin/owner
        if (data.user.role === 'admin' || data.user.role === 'owner') {
          var adminLink = nav.querySelector('.tamma-admin-link');
          if (adminLink) adminLink.style.display = 'inline-block';
        }
      }
    })
    .catch(function() {
      // If API is unreachable, show fallback
      document.getElementById('tamma-user-name').textContent = 'User';
    });

  // Show nav bar after setup
  nav.style.display = 'flex';

  // Close user menu on outside click
  document.addEventListener('click', function(e) {
    var menu = nav.querySelector('.tamma-user-menu');
    var userEl = nav.querySelector('.tamma-user');
    if (!userEl.contains(e.target)) {
      menu.classList.remove('open');
    }
  });
})();
</script>
```

### Step 2: nginx sub_filter Injection

nginx's `sub_filter` module replaces strings in response bodies. We inject the navigation bar HTML just after `<body>` in the upstream response.

**For logs.tamma.dev:**

```nginx
# In the logs.tamma.dev server block
sub_filter_once on;
sub_filter_types text/html;
sub_filter '</head>' '<link rel="stylesheet" href="/tamma-nav.css" /></head>';
sub_filter '<body' '<body style="padding-top:48px"';
sub_filter '</body>' '<!--#include virtual="/tamma-nav-include" --></body>';
```

However, `sub_filter` with includes is complex. A simpler approach is to serve the nav HTML from a dedicated location and inject it inline:

```nginx
# logs.tamma.dev — inject nav bar
location / {
    auth_request /oauth2/auth;
    # ... auth headers ...

    proxy_pass http://opensearch-dashboards:5601;
    # ... proxy headers ...

    # Inject navigation bar
    sub_filter '</body>' '<script src="https://app.tamma.dev/nav/tamma-nav.js"></script></body>';
    sub_filter_once on;
    sub_filter_types text/html;
    proxy_set_header Accept-Encoding "";  # Required: disable compression for sub_filter
}
```

**Alternative (recommended): Serve nav as a script that self-injects:**

Create a standalone JS file that, when loaded, injects the full nav bar HTML into the page. This is simpler to maintain than inline `sub_filter`:

```javascript
// docker/nav-header/tamma-nav.js
// Self-injecting navigation bar
(function() {
  var container = document.createElement('div');
  container.innerHTML = '...'; // Full nav bar HTML from Step 1
  document.body.prepend(container.firstElementChild);
  document.body.style.paddingTop = '48px';
  // ... user fetch and active state logic ...
})();
```

Then nginx just injects one `<script>` tag:

```nginx
sub_filter '</head>' '<script src="https://app.tamma.dev/tamma-nav.js" defer></script></head>';
sub_filter_once on;
sub_filter_types text/html;
proxy_set_header Accept-Encoding "";
```

### Step 3: Serve the Nav Script from Tamma Dashboard

The Tamma Dashboard's nginx can serve the `tamma-nav.js` file as a static asset:

```nginx
# In app.tamma.dev server block
location /tamma-nav.js {
    alias /usr/share/nginx/html/tamma-nav.js;
    add_header Cache-Control "public, max-age=300";  # 5-minute cache
    add_header Access-Control-Allow-Origin "https://logs.tamma.dev";
    add_header Access-Control-Allow-Origin "https://elsa.tamma.dev";
}
```

Or serve from the Tamma API as a route, or from a shared static directory mounted in the nginx-proxy container.

### Step 4: React Component for Tamma Dashboard

The Tamma Dashboard uses a native React component (not the injected script) for better integration:

```tsx
// packages/dashboard/src/components/NavHeader.tsx
import { useAuth } from '../hooks/useAuth';

const SERVICES = [
  { key: 'app', label: 'Dashboard', url: 'https://app.tamma.dev' },
  { key: 'elsa', label: 'Workflows', url: 'https://elsa.tamma.dev' },
  { key: 'logs', label: 'Logs', url: 'https://logs.tamma.dev' },
];

export function NavHeader() {
  const { user } = useAuth();
  const [menuOpen, setMenuOpen] = useState(false);

  return (
    <nav className="tamma-nav-bar">
      <span className="tamma-logo">Tamma</span>

      {SERVICES.map(svc => (
        <a key={svc.key} href={svc.url}
           className={window.location.hostname.startsWith(svc.key) ? 'active' : ''}>
          {svc.label}
        </a>
      ))}

      {(user?.role === 'admin' || user?.role === 'owner') && (
        <a href="/admin">Admin</a>
      )}

      <div className="tamma-spacer" />

      {user && (
        <div className="tamma-user" onClick={() => setMenuOpen(!menuOpen)}>
          <img className="tamma-avatar"
               src={`https://github.com/${user.username}.png?size=56`}
               alt={user.username} />
          <span>{user.username}</span>

          {menuOpen && (
            <div className="tamma-user-menu">
              <a href="/settings">Settings</a>
              <a href="/oauth2/sign_out">Sign Out</a>
            </div>
          )}
        </div>
      )}
    </nav>
  );
}
```

### Step 5: ELSA Studio Navigation

If the custom Tamma Studio (Story 14.1) exists, add the nav bar to the Blazor `MainLayout.razor`:

```razor
@inherits LayoutComponentBase

<TammaNavBar />

<MudThemeProvider Theme="TammaTheme">
    @Body
</MudThemeProvider>
```

If the custom Studio does not exist yet (still using upstream image), use the nginx `sub_filter` injection approach (same as OpenSearch Dashboards).

### Step 6: CORS for Cross-Subdomain Script Loading

The `tamma-nav.js` is served from `app.tamma.dev` but loaded on `elsa.tamma.dev` and `logs.tamma.dev`. CORS headers are needed:

```nginx
location /tamma-nav.js {
    # ... serve the file ...
    add_header Access-Control-Allow-Origin "*";  # Or enumerate subdomains
    add_header Access-Control-Allow-Methods "GET";
}
```

The `/api/auth/me` endpoint is called cross-origin from `elsa.tamma.dev` and `logs.tamma.dev`. The `tamma_session` cookie has `domain: .tamma.dev` so it is sent cross-subdomain. The API needs to allow CORS from `*.tamma.dev`:

```typescript
// In Tamma API CORS config, add:
origin: [
  'https://app.tamma.dev',
  'https://elsa.tamma.dev',
  'https://logs.tamma.dev',
],
credentials: true,
```

## Logging Requirements

| Event | Level | Output | Notes |
|-------|-------|--------|-------|
| Nav bar script loaded | DEBUG | Browser console | Only in development |
| User info fetch failed | WARN | Browser console | Fallback to "User" display |
| Nav bar injection (nginx) | N/A | Not logged | sub_filter is transparent |

### Sensitive Data Redaction

- GitHub username and avatar URL are public information, safe to display
- No sensitive data in the navigation bar itself

## Testing Strategy

### Manual Verification

1. Visit `https://app.tamma.dev` — nav bar shows with Dashboard highlighted, user avatar visible
2. Click "Workflows" — navigates to `https://elsa.tamma.dev`, nav bar shows with Workflows highlighted
3. Click "Logs" — navigates to `https://logs.tamma.dev`, nav bar shows with Logs highlighted
4. Log in as `member` — Admin link is NOT visible
5. Log in as `admin` or `owner` — Admin link IS visible
6. Click user avatar — dropdown shows Settings and Sign Out
7. Click Sign Out — session cleared, redirected to login
8. Resize browser to mobile width — nav bar is responsive (hamburger menu or condensed layout)
9. Verify OpenSearch Dashboards page content is not obscured by the nav bar (body padding-top applied)
10. Verify ELSA Studio page content is not obscured by the nav bar

### Automated Checks

```bash
# Nav script is served with correct CORS headers
curl -sf -H "Origin: https://elsa.tamma.dev" https://app.tamma.dev/tamma-nav.js -D - | grep Access-Control

# Nav script returns valid JavaScript
curl -sf https://app.tamma.dev/tamma-nav.js | node -e "process.stdin.resume()"  # No syntax errors
```

### Unit Tests

1. `NavHeader.test.tsx` — renders service links, highlights active, shows/hides admin link based on role
2. `NavHeader.test.tsx` — user menu opens/closes on click
3. `NavHeader.test.tsx` — handles missing user gracefully

## Dependencies

- **Story 16.1** (OAuth2 Proxy) — nav bar relies on the shared session cookie for user identity
- Story 14.1 (Custom ELSA Studio) — for native Blazor nav bar integration (optional; nginx injection is the fallback)
- Story 15.1 (OpenSearch Dashboards) — logs.tamma.dev must exist

## Estimated Effort

| Task | Hours |
|------|-------|
| Self-contained nav bar HTML/CSS/JS | 3 |
| nginx sub_filter configuration | 2 |
| React NavHeader component | 2 |
| CORS configuration | 1 |
| ELSA Studio integration (Blazor or nginx) | 2 |
| Responsive styling | 1 |
| Testing | 1 |
| **Total** | **12 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation | Architecture Team |
