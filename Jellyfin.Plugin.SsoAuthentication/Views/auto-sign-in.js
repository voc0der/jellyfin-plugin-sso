// Automatic sign-in, for Jellyfin Web. SSO Authentication adds this script to Jellyfin Web's page
// while automatic sign-in is turned on in its settings, on a tag that carries the address sign-in
// starts at. Someone who opens Jellyfin Web without being signed in goes there, and so to the
// provider, instead of to Jellyfin's login page. Only the browser knows whether it is signed in,
// so it is decided here. The browser goes:
//
// - straight away, before Jellyfin Web starts, when it holds no session at all and was not opened
//   on one of the pages Jellyfin Web shows people who are not signed in;
// - otherwise when Jellyfin Web sends it to the login page because the page it was opened at
//   needs someone signed in, which Jellyfin Web says by naming the server and that page
//   (serverid and url).
//
// And it stays:
//
// - inside a frame: the plugin's own sign-in page loads Jellyfin Web in one, to set up storage;
// - in an app that shows Jellyfin Web, which gives it a NativeShell, and in an Android WebView,
//   which is how the Android app shows it, before its NativeShell is in place;
// - on the login page reached any other way, by signing out or by opening #/login, and then in
//   this browser until someone signs in;
// - in a tab where going to the provider brought no session back, or one Jellyfin Web refused,
//   until someone signs in. A sign-in that does not work ends on the login page, never in a loop.
//
// Back from the provider, the browser opens the page it was first opened at.
// A sign-in button remains on the login page for apps, explicit logout and failed attempts.
//
// Plain ES5: Jellyfin Web's page loads this in every browser that opens it, and one that cannot
// run it is left with the login page.
(function () {
  "use strict";

  var script = document.currentScript;
  var start = script && parseStart(script.getAttribute("data-sso-auto-sign-in"));
  var fallback = script && script.hasAttribute("data-sso-auto-fallback");
  if ((!start && !fallback) || window.top !== window.self) {
    return;
  }

  // The tag names a provider, not an arbitrary redirect. Rebuild only the local start route,
  // keeping the provider in one encoded segment even if the attribute is changed in the DOM.
  function parseStart(value) {
    var parts = /^\.\.\/sso\/(OID|SAML)\/start\/(.+)$/.exec(value || "");
    if (!parts) {
      return null;
    }

    try {
      var provider = decodeURIComponent(parts[2]);
      if (provider === "." || provider === "..") {
        return null;
      }

      return {
        protocol: parts[1] === "SAML" ? "SAML" : "OID",
        provider: encodeURIComponent(provider),
      };
    } catch (e) {
      return null;
    }
  }

  // In localStorage: this browser signed out, or opened the login page itself.
  var SIGNED_OUT = "sso-auto-sign-in-signed-out";

  // In sessionStorage: this tab went to the provider.
  var TRIED = "sso-auto-sign-in-tried";

  // How long after a session comes back from the provider a trip to the login page means that
  // Jellyfin Web refused it.
  var SETTLE_MS = 60000;

  // Pages Jellyfin Web shows people who are not signed in, besides its setup wizard.
  var PUBLIC_PAGES = [
    "/addserver",
    "/selectserver",
    "/login",
    "/forgotpassword",
    "/forgotpasswordpin",
  ];

  function storage(name) {
    try {
      return window[name] || null;
    } catch (e) {
      // Turned off in the browser's settings.
      return null;
    }
  }

  var local = storage("localStorage");
  var session = storage("sessionStorage");

  function read(store, key) {
    try {
      var value = JSON.parse(store.getItem(key));
      return value !== null && typeof value === "object" ? value : null;
    } catch (e) {
      return null;
    }
  }

  function write(store, key, value) {
    try {
      if (value) {
        store.setItem(key, JSON.stringify(value));
      } else {
        store.removeItem(key);
      }

      return true;
    } catch (e) {
      return false;
    }
  }

  if (fallback) {
    var attempt = read(session, TRIED);
    if (attempt && !attempt.back) {
      // The attribute is only a marker: its text cannot choose another destination.
      window.location.replace("../../../web/index.html#/login");
    }

    return;
  }

  // The session Jellyfin Web keeps, or "".
  function storedToken() {
    var credentials = read(local, "jellyfin_credentials");
    var servers = (credentials && credentials.Servers) || [];
    for (var i = 0; i < servers.length; i++) {
      var token = servers[i] && servers[i].AccessToken;
      if (typeof token === "string" && token) {
        return token;
      }
    }

    return "";
  }

  // Enough of a session to tell a new one from it, without keeping a copy.
  function fingerprint(token) {
    var hash = 2166136261;
    for (var i = 0; i < token.length; i++) {
      hash = Math.imul(hash ^ token.charCodeAt(i), 16777619) >>> 0;
    }

    return token ? hash.toString(16) : "";
  }

  // A note holds until a session it did not see is stored: until someone signs in.
  function holds(note) {
    var token = storedToken();
    return !!note && (!token || fingerprint(token) === note.token);
  }

  // The page in a Jellyfin Web address: "/details?id=1" for "#/details?id=1".
  function pageOf(hash) {
    return hash.replace(/^#!?/, "");
  }

  function isPublic(page) {
    var path = page.split("?")[0].toLowerCase();
    return PUBLIC_PAGES.indexOf(path) !== -1 || path.indexOf("/wizard") === 0;
  }

  function stays() {
    var tried = read(session, TRIED);
    return (
      !!window.NativeShell ||
      /; wv\)/.test(navigator.userAgent) ||
      holds(read(local, SIGNED_OUT)) ||
      (holds(tried) && !(tried.back && Date.now() - tried.at > SETTLE_MS))
    );
  }

  function go(page, manual) {
    // Noted first: a tab that cannot note that it went could go round in circles.
    var noted = write(session, TRIED, {
      token: fingerprint(storedToken()),
      at: Date.now(),
      page: page,
    });
    if (noted || manual) {
      window.location.assign("../sso/" + start.protocol + "/start/" + start.provider);
    }
  }

  function addSignInButton(view) {
    var container = view.querySelector(".readOnlyContent");
    if (!container || view.querySelector(".btnSsoSignIn")) {
      return;
    }

    // Outside the disclaimer: Jellyfin sanitizes and replaces that content asynchronously,
    // and the password form may be hidden on servers that only allow SSO.
    var button = document.createElement("button");
    button.type = "button";
    button.className = "raised button-submit block emby-button btnSsoSignIn";
    button.textContent = "Sign in with SSO";
    var error = document.createElement("p");
    error.setAttribute("role", "alert");
    error.hidden = true;

    button.addEventListener("click", function () {
      try {
        // Native clients use AppHost's device ID without writing Jellyfin Web's storage key.
        // The SSO completion page needs that key, and must create the session for the same device.
        var host = window.NativeShell && window.NativeShell.AppHost;
        if (host && typeof host.deviceId === "function") {
          var deviceId = host.deviceId();
          if (typeof deviceId === "string" && deviceId) {
            local.setItem("_deviceId2", deviceId);
          }
        }

        if (!local) {
          throw new Error("Browser storage is unavailable");
        }
      } catch (e) {
        error.textContent = "Unable to prepare sign-in. Reopen the app or browser and try again.";
        error.hidden = false;
        return;
      }

      button.disabled = true;
      go("", true);
      // Allow another click if navigation is interrupted, including a return from the provider.
      window.setTimeout(function () { button.disabled = false; }, 3000);
    });

    container.insertBefore(error, container.firstChild);
    container.insertBefore(button, error);
  }

  var token = storedToken();

  var signedOut = read(local, SIGNED_OUT);
  if (signedOut && !holds(signedOut)) {
    write(local, SIGNED_OUT, null);
  }

  var tried = read(session, TRIED);
  if (tried && token && fingerprint(token) !== tried.token) {
    // Signed in since: back from the provider, or some other way. Noted, so that if Jellyfin Web
    // refuses the session and sends the browser to its login page, the note holds again.
    write(session, TRIED, { token: fingerprint(token), at: Date.now(), back: true });

    // The plugin's sign-in page ends at Jellyfin Web's start page. Open the page that was asked
    // for instead, before Jellyfin Web reads the address.
    var page = tried.page;
    if (
      !tried.back &&
      typeof page === "string" &&
      /^\/[^/]/.test(page) &&
      !isPublic(page) &&
      pageOf(window.location.hash) === ""
    ) {
      window.history.replaceState(window.history.state, "", "#" + page);
    }
  }

  var opened = pageOf(window.location.hash);
  if (!token && !isPublic(opened) && !stays()) {
    go(opened);
  }

  document.addEventListener("viewshow", function (event) {
    if (!event.target || event.target.id !== "loginPage") {
      return;
    }

    addSignInButton(event.target);

    var params = (event.detail && event.detail.params) || {};
    if (!params.serverid || !params.url) {
      // Signed out, or opened on purpose: this browser stays on the login page until someone
      // signs in.
      write(local, SIGNED_OUT, { token: fingerprint(storedToken()) });
    } else if (!stays()) {
      go(params.url);
    }
  });
})();
