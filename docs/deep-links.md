# Deep Link testing

The Deep Link tab is available in both desktop apps. Select an Android device that is connected and authorized, enter a custom-scheme URI, and press **Launch** or Enter. **Stop** cancels pending work; it cannot undo an intent already delivered to an app.

## Supported links and capabilities

- Custom links such as `myapp://orders/123?source=qa` use `android.intent.action.VIEW`.
- Android intents such as `intent://orders/123#Intent;scheme=myapp;package=com.example.app;S.source=qa;end` preserve their declared action and extras. Single intents and Android scalar extras are supported; selectors and unknown fields produce validation warnings.
- The offline policy blocks HTTPS App Links, other restricted schemes, and intent URIs whose effective destination uses a restricted scheme. App behavior after receiving a custom link is controlled by the app.
- Opening links on iOS is unavailable with the bundled pymobiledevice3 implementation. The tab explains this and disables Android operations for iOS devices.
- Handler inspection uses Android's package-manager command. Older or restricted devices may not support it; the tab reports unavailable inspection rather than claiming there are no handlers.

## Preview and targeting

The parsed preview hides destinations and extra values by default. **Reveal full link in preview** reveals them for the current tab. The input itself retains the link you entered.

**Load installed apps** fills the package picker. You can also enter a package manually. Leave it empty to let Android resolve the link. A package that conflicts with an explicit package or component in an intent is rejected before execution. Enable **Include BROWSABLE category** to test browser-style intent matching.

**Inspect handlers** lists activities that match the link and targeting options without launching them. Matching does not guarantee Android will permit access to the activity.

## Results and batch testing

Launch results distinguish a new launch, delivery to an existing activity, bringing an existing task forward, deferred launches, errors, cancellation, and timeout. When available, results include the resolved activity and total/wait timing. Android confirmation does not assert that the app navigated to the expected screen; verify that on the device.

The batch editor accepts one link per line, up to 50 links, with the current package/category options. All lines are validated before the first launch. Tests run sequentially, without automatic retries. A timeout or cancellation stops the batch because the previous link may already have launched. Batches have a five-minute limit.

The last 100 results are retained in memory. Links in history and JSON exports hide their destinations and parameters. **Clear history** removes the in-memory results.

## Presets and privacy

Save a named preset to retain the full link and launch options in memory. Saving the same name updates the preset. Up to 50 presets can be stored.

**Persist presets locally** is an explicit opt-in to saving full links and parameters in the protected application-data directory (`LogPro/DeepLinks/presets.json`). Disabling it removes that saved file while keeping the current presets in memory. Persistence errors are shown; failed deletion must not be treated as successful removal.

Deep-link payloads and raw launch output are omitted from diagnostic logs. History is never persisted automatically, and exports contain sanitized result data.
