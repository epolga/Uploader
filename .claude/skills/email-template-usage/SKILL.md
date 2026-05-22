---
name: email-template-usage
description: Invoke this skill whenever the user asks to change, edit, modify, update, or fix the Uploader's HTML email template (Uploader/Templates/HtmlEmailTemplate.txt) or the App.config setting that points to it. Covers content edits, section reordering, image and link changes, template-path configuration, and "does not contain any sections" troubleshooting. Does NOT cover actually sending the email — the WPF Uploader app handles SES delivery.
---

# Email template usage

Operational rules for editing the Uploader's outbound email template. The template is a flat text file with section-header markers; the WPF Uploader loads it, substitutes tokens, and hands the rendered HTML to SES for delivery.

## Files

- **Template:** `Uploader/Templates/HtmlEmailTemplate.txt` — the HTML body, broken into named sections.
- **Config:** `Uploader/App.config` — `HtmlEmailTemplatePath` key tells the app which template file to load. Changing template files means changing this key, not just the file on disk.

## Section headers (load-bearing — required order)

The template parser splits the file on section-header markers. Every template must contain these headers, in this order, each on its own line:

```
[Subject]
[Greeting]
[BeforeImage]
[ImageWithLink]
[AfterImage]
[Unsubscribe]
[Closing]
[Signature]
```

Missing or reordered headers produce the `"does not contain any sections"` runtime error.

## Token substitution

Body content can reference these tokens — the app replaces them at render time:

- `<pattern_url>` — link to the cross-stitch pattern page
- `<image_url>` — direct URL to the pattern image
- `[FName]` — recipient first name

For images, use `<img src="<image_url>" ...>` with `max-width` / `max-height` attributes to constrain rendering across email clients.

## Best practices

- Avoid duplicate links to the same destination — most email clients show them as separate elements and it reads as spammy.
- Keep formatting simple. Heavy CSS, layout tables-within-tables, and modern selectors render unpredictably across Gmail / Outlook / Apple Mail.
- Always send a test to yourself before any mass send.

## Troubleshooting

| Symptom | First thing to check |
|---|---|
| `"does not contain any sections"` runtime error | The template file is missing one of the required section headers, or a header line has stray whitespace / wrong bracket character. |
| Wrong template loaded | The `HtmlEmailTemplatePath` key in `App.config` — confirm it points to the file you just edited. |
| Token not substituted (`[FName]` shows literally) | Token spelled wrong (case-sensitive on some tokens), or the sending code doesn't pass that field. Grep for the token usage in the C# email code before changing template syntax. |
| Image renders too large / breaks layout | Add `max-width` / `max-height` to the `<img>` — many clients ignore CSS but honor inline attributes. |
