# CLAUDE.md

Project-level guidance for Claude Code when working in this repository.

## Skills to use

- **Email template edits → `email-template-usage` skill.** Whenever the user asks to change, edit, modify, update, or fix the HTML email template (`Uploader/Templates/HtmlEmailTemplate.txt`) or the `HtmlEmailTemplatePath` setting in `Uploader/App.config`, invoke the [`email-template-usage`](.claude/skills/email-template-usage/SKILL.md) skill before making edits. It documents the required section headers, token substitution rules, and the load-vs-render split between the template file and the WPF Uploader's send pipeline. The skill is guidance only — it does not send email.
