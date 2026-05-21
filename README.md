"# Uploader" 

## Changes (unreleased)
- Added a text-only email workflow with a new UI button, templates, personalization, unsubscribe headers, and progress updates.
- Fetch user email recipients can now filter to verified/subscribed users for text-only sends.
- PDF uploads now run Converter.exe on 1/3/5 and upload the generated `.converted.pdf` outputs.
- Pattern preview and info extraction now use `1.pdf` instead of `Proto.pdf`.
- Pinterest board description text now points to Cross-Stitch.com.

## Configuration notes

- AppSettings keys `HtmlEmailTemplatePath` and `TextEmailTemplatePath` control which email template files are loaded.
- You can now set these to the new shared location (recommended):
	- `D:\ann\Git\cross-stitch-platform-docs\plan\uploader\HtmlEmailTemplate.txt`
	- `D:\ann\Git\cross-stitch-platform-docs\plan\uploader\TextEmailTemplate.txt`
- Or keep the old relative paths for legacy use.
- Converter is expected at `D:\ann\Git\Converter\bin\Release\net9.0\Converter.exe` and produces `<name>.converted.pdf`.
