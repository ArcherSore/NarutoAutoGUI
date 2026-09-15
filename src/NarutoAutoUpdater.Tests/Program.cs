if (args.Contains("--engine-fixture")) {
    await EngineClientTests.RunFixtureAsync();
    return;
}
await EngineClientTests.RunAsync();
