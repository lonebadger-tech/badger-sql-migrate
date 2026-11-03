using Badger.SqlMigrate.Core.Plan;

namespace Badger.SqlMigrate.Core.Tests;

public class PlanFileTests
{
    [Fact]
    public void Parses_changes_dependencies_flags_tags_and_notes()
    {
        var plan = PlanFile.Parse("""
            # header comment
            %syntax-version=1
            create-customer @2026-11-03T14:10Z brock  # first
            add-email [create-customer] !notx @2026-11-04T09:02Z alice
            @v1
            drop-old !irreversible
            """);

        Assert.Empty(plan.Errors);
        Assert.Equal(["create-customer", "add-email", "drop-old"], plan.Changes.Select(c => c.Name));
        Assert.Equal("first", plan.Changes[0].Note);
        Assert.Equal("brock", plan.Changes[0].Author);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 14, 10, 0, TimeSpan.Zero), plan.Changes[0].PlannedAt);
        Assert.Equal(["create-customer"], plan.Changes[1].Requires);
        Assert.Equal(ChangeFlags.NoTransaction, plan.Changes[1].Flags);
        Assert.Equal(["v1"], plan.Changes[1].Tags);
        Assert.Equal(ChangeFlags.Irreversible, plan.Changes[2].Flags);
        Assert.Equal(1, plan.ResolveTarget("@v1"));
        Assert.Equal(-1, plan.ResolveTarget("@nope"));
    }

    [Fact]
    public void Format_round_trips()
    {
        var change = new PlanChange("add-email", ["a", "b"], ChangeFlags.NoTransaction | ChangeFlags.Irreversible, new DateTimeOffset(2026, 1, 2, 3, 4, 0, TimeSpan.Zero), "alice", "why", 0);
        var line = PlanFile.FormatChange(change);
        var parsed = PlanFile.Parse(line).Changes.Single();

        Assert.Equal(change.Name, parsed.Name);
        Assert.Equal(change.Requires, parsed.Requires);
        Assert.Equal(change.Flags, parsed.Flags);
        Assert.Equal(change.PlannedAt, parsed.PlannedAt);
        Assert.Equal(change.Author, parsed.Author);
        Assert.Equal(change.Note, parsed.Note);
    }

    [Theory]
    [InlineData("-bad-name", "Invalid change name")]
    [InlineData("ok [dep", "Unclosed")]
    [InlineData("ok !weird", "Unexpected token")]
    [InlineData("ok @notadate", "Invalid timestamp")]
    [InlineData("@tag-without-change", "must follow a change")]
    public void Reports_syntax_errors_with_line_numbers(string text, string expected)
    {
        var error = Assert.Single(PlanFile.Parse(text).Errors);
        Assert.Equal(1, error.LineNumber);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_catches_duplicates_unknown_and_forward_dependencies()
    {
        var problems = PlanValidator.Validate(PlanFile.Parse("""
            a
            b [c]
            c [missing]
            a
            """));

        Assert.Contains(problems, p => p.Contains("'b' requires 'c', which appears later", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("unknown change 'missing'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'a' is already defined", StringComparison.Ordinal));
    }

    /// <summary>
    /// The Flyway "two developers both picked V12" problem: with git's union merge driver both
    /// appended lines survive, and the merged plan is valid because names, not numbers, identify changes.
    /// </summary>
    [Fact]
    public void Union_merge_of_concurrent_appends_is_a_valid_plan()
    {
        const string baseline = "create-customer\n";
        var alice = baseline + "add-customer-email [create-customer] @2026-11-04T09:02Z alice\n";
        var bob = baseline + "add-order-index @2026-11-04T09:15Z bob\n";

        // git merge=union keeps lines from both sides (ours first, then theirs).
        var merged = baseline + alice[baseline.Length..] + bob[baseline.Length..];

        var plan = PlanFile.Parse(merged);
        Assert.Empty(PlanValidator.Validate(plan));
        Assert.Equal(["create-customer", "add-customer-email", "add-order-index"], plan.Changes.Select(c => c.Name));
    }

    [Fact]
    public void Union_merge_of_the_same_name_is_caught()
    {
        var plan = PlanFile.Parse("base\nsame-name @2026-11-04T09:02Z alice\nsame-name @2026-11-04T09:15Z bob\n");
        Assert.Contains(PlanValidator.Validate(plan), p => p.Contains("already defined", StringComparison.Ordinal));
    }
}
