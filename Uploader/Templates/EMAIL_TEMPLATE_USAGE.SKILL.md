# Email Template Usage Skill

## Purpose
Explains how to update and use email templates in this project.

## How to Update the Template
- Edit `Uploader/Templates/HtmlEmailTemplate.txt` for HTML emails.
- Use section headers: [Subject], [Greeting], [BeforeImage], [ImageWithLink], [AfterImage], [Unsubscribe], [Closing], [Signature].
- For images, use `<img src="<image_url>" ...>` and set `max-width`/`max-height` as needed.

## How the App Uses the Template
- The app loads the template path from `App.config` (`HtmlEmailTemplatePath`).
- It replaces tokens like `<pattern_url>`, `<image_url>`, and `[FName]` with actual values.
- The email is sent using the rendered template.

## Best Practices
- Avoid redundant links.
- Keep formatting simple for best email client compatibility.
- Test by sending to yourself before mass mailing.

## Troubleshooting
- If you see “does not contain any sections,” ensure the file starts with a section header.
- If the wrong template is loaded, check the path in `App.config`.
