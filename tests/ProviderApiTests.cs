using System;
using System.IO;
using System.Collections.Generic;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using PactNet.Infrastructure.Outputters;
using PactNet.Output.Xunit;
using PactNet.Verifier;
using PactNet;
using Xunit;
using Xunit.Abstractions;

// This file contains the provider-side contract tests for the API.
// It verifies that the provider (this service) meets the contract expected by its consumers.
// The tests use Pact to validate the API against the consumer contracts stored in the Pact Broker.


namespace tests;

/// <summary>
/// Test class that verifies the provider's API against consumer contracts.
/// Inherits from IDisposable to properly clean up resources.
/// </summary>
public class ProviderApiTests : IDisposable
{
    // Base URI for the provider API being tested
    private string _providerUri { get; }
    
    // URI for the Pact mock service that will be started during tests
    private string _pactServiceUri { get; }
    
    // Web host for the test server
    private IWebHost _webHost { get; }
    
    // Helper for xUnit test output
    private ITestOutputHelper _outputHelper { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderApiTests"/> class.
    /// Sets up the test environment including the test web server.
    /// </summary>
    /// <param name="output">xUnit test output helper for logging</param>
    public ProviderApiTests(ITestOutputHelper output)
    {
        _outputHelper = output;
        
        // Configure provider and pact service URIs
        _providerUri = "http://localhost:9900";
        _pactServiceUri = "http://localhost:9901";

        // Create and start a test web server that will handle provider state setup
        _webHost = WebHost.CreateDefaultBuilder()
            .UseUrls(_pactServiceUri)
            .UseStartup<TestStartup>()
            .Build();

        _webHost.Start();
    }

    /// <summary>
    /// Main test method that verifies the provider against all consumer pacts.
    /// This test will be run by xUnit and will verify the provider's compliance
    /// with all consumer contracts from the configured source (file or broker).
    /// </summary>
    [Fact]
    public void EnsureProviderApiHonoursPactWithConsumer()
    {
        // Arrange - Configure the Pact verifier
        var config = new PactVerifierConfig
        {

            // Configure outputters for test results
            // xUnit 2 doesn't capture console output by default, so we use XunitOutput
            // to ensure test output appears in the test results
            Outputters = new List<IOutput>
                            {
                                new XunitOutput(_outputHelper),  // Send output to xUnit test results
                                new ConsoleOutput()              // Also output to console for local debugging
                            },

            // Set log level to Debug for detailed verification output
            // This helps with troubleshooting test failures
            LogLevel = PactLogLevel.Debug,
        };

        string providerName = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PACT_PROVIDER_NAME"))
                                ? Environment.GetEnvironmentVariable("PACT_PROVIDER_NAME")
                                : "pactflow-example-provider-dotnet";
        IPactVerifier pactVerifier = new PactVerifier(providerName, config);
        string pactUrl = Environment.GetEnvironmentVariable("PACT_URL");
        string pactFile = Environment.GetEnvironmentVariable("PACT_FILE");
        string version = Environment.GetEnvironmentVariable("GIT_COMMIT");
        string branch = Environment.GetEnvironmentVariable("GIT_BRANCH");
        string buildUri = $"{Environment.GetEnvironmentVariable("GITHUB_SERVER_URL")}/{Environment.GetEnvironmentVariable("GITHUB_REPOSITORY")}/actions/runs/{Environment.GetEnvironmentVariable("GITHUB_RUN_ID")}";


        if (pactFile != "" && pactFile != null)
        // Verify against a local pact file (for development/testing without a broker)
        // This mode is used when PACT_FILE environment variable is set
        // Verification results are not published back to a broker in this mode
        {

            pactVerifier.WithHttpEndpoint(new Uri(_providerUri))
            .WithFileSource(new FileInfo(pactUrl))
            .WithProviderStateUrl(new Uri($"{_pactServiceUri}/provider-states"))
            .Verify();
        }
        else if (pactUrl != "" && pactUrl != null)
        // Verify against a specific pact file from a URL (e.g., from a broker or direct URL)
        // This mode is used when PACT_URL environment variable is set
        // Verification results may be published back to the broker if configured
        {
            pactVerifier.WithHttpEndpoint(new Uri(_providerUri))
            .WithUriSource(new Uri(pactUrl), options =>
            {
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PACT_BROKER_TOKEN")))
                {
                    options.TokenAuthentication(Environment.GetEnvironmentVariable("PACT_BROKER_TOKEN"));
                }
                else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PACT_BROKER_USERNAME")))
                {
                    options.BasicAuthentication(Environment.GetEnvironmentVariable("PACT_BROKER_USERNAME"), Environment.GetEnvironmentVariable("PACT_BROKER_PASSWORD"));
                }
                options.PublishResults(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PACT_BROKER_PUBLISH_VERIFICATION_RESULTS")), version, results =>
                    {
                        results.ProviderBranch(branch)
                        .BuildUri(new Uri(buildUri));
                    });
            })
            .WithProviderStateUrl(new Uri($"{_pactServiceUri}/provider-states"))
            .Verify();
        }
        else
        {
            // Verify against all relevant pacts from the Pact Broker
            // This is the main production mode where the provider verifies against
            // all consumer pacts that match the configured selectors
            // Verification results are published back to the broker if configured
            if (Environment.GetEnvironmentVariable("PACT_BROKER_BASE_URL") == null){
                throw new InvalidOperationException("PACT_BROKER_BASE_URL environment variable is not set.");
            }

            pactVerifier.WithHttpEndpoint(new Uri(_providerUri))
                .WithPactBrokerSource(new Uri(Environment.GetEnvironmentVariable("PACT_BROKER_BASE_URL")), options =>
                {
                    options.ConsumerVersionSelectors(
                                new ConsumerVersionSelector { DeployedOrReleased = true },
                                new ConsumerVersionSelector { MainBranch = true },
                                new ConsumerVersionSelector { MatchingBranch = true }
                            )
                            .ProviderBranch(branch)
                            .PublishResults(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PACT_BROKER_PUBLISH_VERIFICATION_RESULTS")), version, results =>
                            {
                                results.ProviderBranch(branch)
                               .BuildUri(new Uri(buildUri));
                            })
                            .EnablePending()
                            .IncludeWipPactsSince(new DateTime(2022, 1, 1));
                    // Conditionally set authentication depending on if you are using an Pact Broker / PactFlow Broker
                    // You may not have credentials with your own broker.
                    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PACT_BROKER_TOKEN")))
                    {
                        options.TokenAuthentication(Environment.GetEnvironmentVariable("PACT_BROKER_TOKEN"));
                    }
                    else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PACT_BROKER_USERNAME")))
                    {
                        options.BasicAuthentication(Environment.GetEnvironmentVariable("PACT_BROKER_USERNAME"), Environment.GetEnvironmentVariable("PACT_BROKER_PASSWORD"));
                    }

                })
                .WithProviderStateUrl(new Uri($"{_pactServiceUri}/provider-states"))
                .Verify();
        }



    }

    #region IDisposable Support

    // Track whether Dispose has been called
    private bool _disposed = false;

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _webHost.Dispose();
        }

        _disposed = true;
    }

    /// <summary>
    /// Public implementation of Dispose pattern callable by consumers.
    /// </summary>
    public void Dispose()
    {
        // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        Dispose(true);

        GC.SuppressFinalize(this);
    }

    #endregion
}
