/**
 * Tamma Unified Navigation Bar — Self-injecting script.
 *
 * When loaded via <script src="https://admin.tamma.dev/tamma-nav.js">,
 * this script fetches the nav HTML snippet and injects it into the page.
 * Used by nginx sub_filter to add the nav bar to third-party dashboards
 * (OpenSearch Dashboards, etc.) without modifying their source.
 */
(function() {
  'use strict';

  // Prevent double-injection
  if (document.getElementById('tamma-nav')) return;

  var NAV_BASE = 'https://admin.tamma.dev';

  // Fetch the nav HTML snippet and inject it
  var xhr = new XMLHttpRequest();
  xhr.open('GET', NAV_BASE + '/tamma-nav.html', true);
  xhr.onload = function() {
    if (xhr.status !== 200) return;
    var wrapper = document.createElement('div');
    wrapper.innerHTML = xhr.responseText;
    // Insert all child nodes (the nav div, style, script) at the top of body
    while (wrapper.firstChild) {
      document.body.insertBefore(wrapper.firstChild, document.body.firstChild);
    }
    // The inline <script> in tamma-nav.html won't auto-execute when
    // inserted via innerHTML. Run it manually.
    var scripts = document.getElementById('tamma-nav');
    if (scripts) {
      var inlineScript = scripts.parentNode.querySelector('script');
      if (inlineScript) {
        var s = document.createElement('script');
        s.textContent = inlineScript.textContent;
        document.body.appendChild(s);
        inlineScript.remove();
      }
    }
    // Ensure body padding
    document.body.style.paddingTop = '48px';
  };
  xhr.send();
})();
