namespace Prescriva.Agent.Domain.Playbooks;

/// <summary>Refuses a playbook that cannot run or that could press a button the Agent must never press.</summary>
public static class PlaybookValidator
{
    public static IReadOnlyList<PlaybookValidationError> Validate(Playbook? playbook)
    {
        var errors = new List<PlaybookValidationError>();
        if (playbook is null)
        {
            errors.Add(new("MISSING_PLAYBOOK", "$", "No playbook."));
            return errors;
        }

        if (string.IsNullOrWhiteSpace(playbook.Id))
        {
            errors.Add(new("MISSING_ID", "id", "A playbook needs an ID."));
        }

        if (string.IsNullOrWhiteSpace(playbook.ProcessIdentity))
        {
            errors.Add(new("MISSING_PROGRAM", "processIdentity", "A playbook needs the program it drives."));
        }

        var steps = playbook.Steps.IsDefault ? [] : playbook.Steps;
        if (steps.Length == 0)
        {
            errors.Add(new("NO_STEPS", "steps", "A playbook needs at least one step."));
        }

        Duplicates(steps.Select(step => step.Id), "DUPLICATE_STEP_ID", "steps", errors);
        for (var index = 0; index < steps.Length; index++)
        {
            ValidateStep(playbook, steps[index], $"steps[{index}]", errors);
        }

        var dialogs = playbook.Dialogs.IsDefault ? [] : playbook.Dialogs;
        Duplicates(dialogs.Select(rule => rule.Id), "DUPLICATE_DIALOG_RULE_ID", "dialogs", errors);
        for (var index = 0; index < dialogs.Length; index++)
        {
            ValidateRule(playbook, dialogs[index], $"dialogs[{index}]", errors);
        }

        return errors;
    }

    private static void ValidateStep(Playbook playbook, PlaybookStep step, string path, List<PlaybookValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(step.Id))
        {
            errors.Add(new("MISSING_STEP_ID", path, "A step needs an ID."));
        }

        if (string.IsNullOrWhiteSpace(step.Target))
        {
            errors.Add(new("MISSING_TARGET", path, "A step needs the window path of its control."));
        }

        switch (step.Kind)
        {
            case PlaybookStepKind.SetField:
                if (string.IsNullOrWhiteSpace(step.Value))
                {
                    errors.Add(new("MISSING_VALUE", path, "A field step needs a value template."));
                }
                else
                {
                    Template(step.Value, path + ".value", errors);
                }

                break;
            case PlaybookStepKind.GridRows:
                if (string.IsNullOrWhiteSpace(step.Rows))
                {
                    errors.Add(new("MISSING_ROWS", path, "A grid step needs the name of its list of rows."));
                }

                var cells = step.Cells.IsDefault ? [] : step.Cells;
                if (cells.Length == 0)
                {
                    errors.Add(new("MISSING_CELLS", path, "A grid step needs its cells."));
                }

                for (var index = 0; index < cells.Length; index++)
                {
                    if (string.IsNullOrWhiteSpace(cells[index]))
                    {
                        // An empty cell would send Enter on an empty grid row - which saves in FórmulaCerta.
                        errors.Add(new("EMPTY_CELL_TEMPLATE", $"{path}.cells[{index}]", "A grid cell needs a value template."));
                    }
                    else
                    {
                        Template(cells[index], $"{path}.cells[{index}]", errors);
                    }
                }

                break;
            case PlaybookStepKind.Click:
                break;
            case PlaybookStepKind.ReadField:
                if (string.IsNullOrWhiteSpace(step.CaptureAs))
                {
                    errors.Add(new("MISSING_CAPTURE_NAME", path, "A read step needs the name to keep the value under."));
                }

                break;
            default:
                errors.Add(new("UNKNOWN_STEP_KIND", path, "Unknown step kind."));
                break;
        }
    }

    private static void ValidateRule(Playbook playbook, DialogRule rule, string path, List<PlaybookValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(rule.Id))
        {
            errors.Add(new("MISSING_DIALOG_RULE_ID", path, "A dialog rule needs an ID."));
        }

        if (string.IsNullOrWhiteSpace(rule.ClassName) || string.IsNullOrWhiteSpace(rule.TitleStartsWith))
        {
            errors.Add(new("INCOMPLETE_DIALOG_RULE", path, "A dialog rule needs the window class and the start of its title."));
        }

        if (rule.Response is DialogResponse.Press or DialogResponse.FillAndPress)
        {
            if (string.IsNullOrWhiteSpace(rule.Button))
            {
                errors.Add(new("MISSING_BUTTON", path, "A rule that answers needs the button to press."));
            }
            else if (!PlaybookTemplate.IsValid(rule.Button))
            {
                errors.Add(new("INVALID_TEMPLATE", path + ".button", "Placeholders are written {name}."));
            }
            else if (Playbook.IsForbidden(rule.Button, playbook.AllForbiddenButtons))
            {
                errors.Add(new("FORBIDDEN_BUTTON", path + ".button", "The Agent never presses this button."));
            }
        }

        if (!string.IsNullOrWhiteSpace(rule.CaptureField) && string.IsNullOrWhiteSpace(rule.CaptureAs))
        {
            errors.Add(new("MISSING_CAPTURE_NAME", path, "A captured dialog field needs the name to keep the value under."));
        }

        if (rule.Response == DialogResponse.FillAndPress)
        {
            if (string.IsNullOrWhiteSpace(rule.Field) || string.IsNullOrWhiteSpace(rule.Value))
            {
                errors.Add(new("MISSING_DIALOG_FIELD", path, "A fill rule needs the dialog field and the value."));
            }
            else
            {
                Template(rule.Value, path + ".value", errors);
            }
        }
    }

    private static void Template(string template, string path, List<PlaybookValidationError> errors)
    {
        if (!PlaybookTemplate.IsValid(template))
        {
            errors.Add(new("INVALID_TEMPLATE", path, "Placeholders are written {name}."));
        }
    }

    private static void Duplicates(IEnumerable<string> ids, string code, string path, List<PlaybookValidationError> errors)
    {
        foreach (var id in ids.Where(id => !string.IsNullOrWhiteSpace(id)).GroupBy(id => id, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            errors.Add(new(code, path, $"'{id.Key}' is used more than once."));
        }
    }
}
