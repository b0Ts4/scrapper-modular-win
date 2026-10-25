using System.Collections.Immutable;
using Prescriva.Agent.Domain.Playbooks;

namespace Prescriva.Agent.Application.Jobs;

/// <summary>
/// Turns a "launch this quote" request into the data of one playbook run per formula (plan 22).
/// Every formula but the last answers "Mais Fórmula(s)?" with Sim. At the assisted level only the
/// first formula is filled and nothing is saved.
/// </summary>
public static class QuoteRuns
{
    public static IReadOnlyList<PlaybookData> Build(LaunchQuoteRequest request, RobotSettings settings)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);
        if (request.Formulas.IsDefaultOrEmpty)
        {
            throw new JobRequestException("NO_FORMULAS");
        }

        if (request.Formulas.Any(formula => formula.Components.IsDefaultOrEmpty))
        {
            throw new JobRequestException("NO_COMPONENTS");
        }

        var saves = settings.Level == RobotLevel.Saves;
        var formulas = saves ? request.Formulas : request.Formulas.Take(1).ToImmutableArray();
        return formulas.Select((formula, index) =>
        {
            var values = ImmutableDictionary<string, string>.Empty
                .Add("volume", formula.Volume)
                .Add("volume_unidade", formula.VolumeUnit)
                .Add("funcionario", settings.EmployeeCode)
                .Add("salvar", saves ? "sim" : "não")
                // The robot opens a new quote itself (Novo Orç.) only for the first formula of the job.
                .Add("abrir", index == 0 ? "sim" : "não")
                .Add("qsp", formula.Qsp == true ? "Sim" : "Não")
                .Add("mais_formulas", index < formulas.Length - 1 ? "Sim" : "Não");
            if (!string.IsNullOrWhiteSpace(formula.Dosage))
            {
                values = values.Add("posologia", formula.Dosage);
            }

            var rows = formula.Components.Select(component => ImmutableDictionary<string, string>.Empty
                    .Add("codigo", component.Code)
                    .Add("quantidade", component.Quantity)
                    .Add("unidade", component.Unit)
                    .Add("descricao", component.ExpectedDescription ?? string.Empty))
                .ToImmutableArray();
            return new PlaybookData(values, ImmutableDictionary<string, ImmutableArray<ImmutableDictionary<string, string>>>.Empty.Add("componentes", rows));
        }).ToArray();
    }
}
