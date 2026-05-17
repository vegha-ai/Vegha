using System.Text;
using Vegha.Core.Flow;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Flow;

public class IterationDataSourceTests
{
    private static async Task<string> WriteTempAsync(string content, string ext)
    {
        var path = Path.GetTempFileName();
        var renamed = Path.ChangeExtension(path, ext);
        File.Move(path, renamed);
        await File.WriteAllTextAsync(renamed, content);
        return renamed;
    }

    [Fact]
    public async Task Csv_header_drives_column_names()
    {
        var path = await WriteTempAsync("name,age\nalice,30\nbob,42\n", ".csv");
        try
        {
            var ds = await IterationDataSource.LoadCsvAsync(path);
            ds.RowCount.Should().Be(2);
            ds.Columns.Should().Equal("name", "age");
            ds.GetRow(0)["name"].Should().Be("alice");
            ds.GetRow(0)["age"].Should().Be("30");
            ds.GetRow(1)["name"].Should().Be("bob");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Csv_handles_quoted_commas_and_embedded_quotes()
    {
        var content =
            "name,note\n" +
            "\"alice\",\"says \"\"hi\"\"\"\n" +
            "\"bob,jr\",\"plain note, with comma\"\n";
        var path = await WriteTempAsync(content, ".csv");
        try
        {
            var ds = await IterationDataSource.LoadCsvAsync(path);
            ds.RowCount.Should().Be(2);
            ds.GetRow(0)["note"].Should().Be("says \"hi\"");
            ds.GetRow(1)["name"].Should().Be("bob,jr");
            ds.GetRow(1)["note"].Should().Be("plain note, with comma");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Csv_strips_bom_and_skips_blank_lines()
    {
        var bom = "﻿";
        var path = await WriteTempAsync(bom + "k\nv1\n\nv2\n", ".csv");
        try
        {
            var ds = await IterationDataSource.LoadCsvAsync(path);
            ds.RowCount.Should().Be(2);
            ds.GetRow(0)["k"].Should().Be("v1");
            ds.GetRow(1)["k"].Should().Be("v2");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Json_array_of_objects_loads()
    {
        var path = await WriteTempAsync(
            "[{\"name\":\"alice\",\"age\":30,\"admin\":true},{\"name\":\"bob\"}]", ".json");
        try
        {
            var ds = await IterationDataSource.LoadJsonAsync(path);
            ds.RowCount.Should().Be(2);
            ds.GetRow(0)["name"].Should().Be("alice");
            ds.GetRow(0)["age"].Should().Be("30");
            ds.GetRow(0)["admin"].Should().Be("true");
            ds.GetRow(1)["name"].Should().Be("bob");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Json_non_array_root_is_rejected()
    {
        var path = await WriteTempAsync("{\"not\":\"an array\"}", ".json");
        try
        {
            Func<Task> act = () => IterationDataSource.LoadJsonAsync(path);
            await act.Should().ThrowAsync<InvalidDataException>();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Missing_file_throws_FileNotFound()
    {
        Func<Task> act = () => IterationDataSource.LoadAsync(Path.Combine(Path.GetTempPath(), "nope_" + Guid.NewGuid() + ".csv"));
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task LoadAsync_auto_detects_format_by_extension()
    {
        var csvPath = await WriteTempAsync("k\nv\n", ".csv");
        var jsonPath = await WriteTempAsync("[{\"k\":\"v\"}]", ".json");
        try
        {
            var csv = await IterationDataSource.LoadAsync(csvPath);
            var json = await IterationDataSource.LoadAsync(jsonPath);
            csv.GetRow(0)["k"].Should().Be("v");
            json.GetRow(0)["k"].Should().Be("v");
        }
        finally { File.Delete(csvPath); File.Delete(jsonPath); }
    }

    [Fact]
    public async Task OutOfRange_index_returns_empty_dict()
    {
        var path = await WriteTempAsync("k\nv\n", ".csv");
        try
        {
            var ds = await IterationDataSource.LoadCsvAsync(path);
            ds.GetRow(99).Should().BeEmpty();
            ds.GetRow(-1).Should().BeEmpty();
        }
        finally { File.Delete(path); }
    }
}
