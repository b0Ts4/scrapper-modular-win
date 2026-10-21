using System.Collections.Immutable;
using Prescriva.Agent.Domain.Playbooks;

namespace Prescriva.Agent.Domain.Tests.Playbooks;

public sealed class PlaybookTemplateTests
{
    private static readonly ImmutableDictionary<string, string> Values =
        ImmutableDictionary<string, string>.Empty.Add("volume", "60").Add("unidade", "CAP");

    [Fact]
    public void Placeholders_are_replaced_by_values_and_text_is_kept()
    {
        Assert.Equal("60 CAP", PlaybookTemplate.Resolve("{volume} {unidade}", Values));
        Assert.Equal("19", PlaybookTemplate.Resolve("19", Values));
    }

    [Fact]
    public void A_missing_value_is_an_error_naming_the_placeholder()
    {
        var error = Assert.Throws<PlaybookDataException>(() => PlaybookTemplate.Resolve("{posologia}", Values));

        Assert.Equal("posologia", error.Name);
    }

    [Fact]
    public void Placeholder_names_are_listed()
    {
        Assert.Equal(["volume", "unidade"], PlaybookTemplate.Names("{volume} x {unidade}"));
    }

    [Theory]
    [InlineData("{volume", false)]
    [InlineData("volume}", false)]
    [InlineData("{}", false)]
    [InlineData("{a{b}}", false)]
    [InlineData("{a} {b}", true)]
    [InlineData("texto", true)]
    public void Template_syntax_is_checked(string template, bool valid)
    {
        Assert.Equal(valid, PlaybookTemplate.IsValid(template));
    }
}
