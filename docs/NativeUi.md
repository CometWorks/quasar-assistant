# Native Quasar UI

Use semantic HTML, Blazor event binding, and scoped CSS for ordinary plugin UI.
The template and preview host have no MudBlazor dependency. Custom canvas,
WebGL, and Three.js surfaces keep their existing rendering systems.

## Appearance

Inherit `--quasar-palette-*` variables from the current Quasar shell. They reflect
saved light/dark branding, custom presets, and live appearance changes. Do not
cache their values in C# or introduce a separate production palette.

```css
.my-plugin-panel {
    color: var(--quasar-palette-text-primary);
    background: var(--quasar-palette-surface);
    border: 1px solid var(--quasar-palette-lines-default);
}
.my-plugin-action {
    background: var(--quasar-palette-primary);
    color: var(--quasar-palette-primary-contrast-text);
}
```

Other useful tokens include `background`, `background-gray`, `text-secondary`,
`lines-inputs`, `table-lines`, `divider`, `hover-background`,
`hover-contrast-text`, and the `info`, `success`, `warning`, and `error` colors
with their corresponding `*-contrast-text` values. Host tokens are inherited
through the DOM, so a native `<dialog>` in the shell keeps the selected palette
when the browser puts it in the top layer. The standalone preview supplies
sample light/dark palettes; production styling comes from the host.

Declare plugin stylesheets in `quasar-plugin.json` relative to `staticAssets`.
Quasar injects them after core CSS. Scope selectors to your plugin to avoid
changing other pages. Installed plugin assets use `/_quasar/plugins/<id>/`;
`/_content/<assembly>/` is the Razor class library path used by the preview host.

## Controls and behavior

- Use `<button type="button">` for actions, `<a>`/`NavLink` for navigation,
  and `<form>` with a submit button for keyboard-friendly form submission.
- Label inputs and icon buttons. Retain existing SVG paths where possible; the
  template's `TemplateIcons` and `TemplateIcon` reuse the original Material
  icons and include their Apache license.
- Use native `<details>`, `<progress>`, and `<table>` where appropriate. Keep
  wide tables inside a scrolling container and use column/row header semantics.
- Use native `<dialog>.showModal()` for modal focus trapping and Escape behavior.
  Handle cancellation in the component, close before removing the element so
  focus returns to its trigger, and dispose JS listeners and references.
- Use native browser constraints or Blazor validation for form feedback. Keep
  service/API validation, authorization checks, confirmation consequences,
  draft protection, and concurrency/busy guards at their existing boundaries.
- Show asynchronous errors and success messages with `role="alert"` or
  `role="status"`. Dispose event subscriptions, timers, and JS interop references.

Build both the preview host and Quasar adapter. Check light/dark modes, custom
branding, keyboard navigation, and narrow layouts before publishing a pinned
plugin revision. Existing Mud-dependent plugins still need their own migration
before Quasar can remove the compatibility providers and assets.
