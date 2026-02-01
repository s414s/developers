using ExchangeRateUpdater.Contracts;
using ExchangeRateUpdater.Exceptions;
using ExchangeRateUpdater.Providers.CNB.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ExchangeRateUpdater.Providers.CNB;

internal class CNBCurrencyExchangeProvider : IExchangeRateProvider
{
    private static readonly Currency _destinationCurrency = new("CZK");
    private static readonly JsonSerializerOptions _jsonSerializerOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly MemoryCacheEntryOptions _cacheOptions;
    private readonly HttpClient _httpClient;
    private readonly ILogger<CNBCurrencyExchangeProvider> _logger;
    private readonly IMemoryCache _cache;

    public CNBCurrencyExchangeProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<CNBCurrencyExchangeProvider> logger,
        IMemoryCache cache,
        IOptions<CNBExchangeRateProviderOptions> options)
    {
        _httpClient = httpClientFactory.CreateClient(Constants.CNBApiClientName);
        _logger = logger;
        _cache = cache;
        _cacheOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = options.Value.CacheAbsoluteExpiration,
            SlidingExpiration = options.Value.CacheSlidingExpiration
        };
    }

    public async Task<IEnumerable<ExchangeRate>> GetExchangeRatesAsync(IEnumerable<Currency> currencies)
    {
        try
        {
            var sourceCurrencies = currencies.ToHashSet();

            var targetDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

            var cacheKey = $"{nameof(CNBCurrencyExchangeProvider)}_{targetDate}";

            if (!_cache.TryGetValue<List<ExchangeRate>>(cacheKey, out var cachedRates))
            {
                var response = await _httpClient.GetAsync($"/cnbapi/exrates/daily?date={targetDate}&lang=EN");
                response.EnsureSuccessStatusCode();

                var responseContent = await response.Content.ReadAsStringAsync();
                var cnbExchangeRates = JsonSerializer.Deserialize<CNBExchangeRateResponse>(responseContent, _jsonSerializerOptions);

                if (cnbExchangeRates is null || cnbExchangeRates.Rates is null)
                {
                    throw new ExchangeRateProviderException("Error getting exchange rates from source, bad expected data");
                }

                cachedRates = cnbExchangeRates.Rates.Select(x => new ExchangeRate(
                        sourceCurrency: new Currency(x.CurrencyCode),
                        targetCurrency: _destinationCurrency,
                        value: x.Rate))
                    .ToList();

                _cache.Set(cacheKey, cachedRates, _cacheOptions);
            }

            return cachedRates.Where(x => sourceCurrencies.Contains(x.SourceCurrency));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching data from {Provider}", nameof(CNBCurrencyExchangeProvider));
            throw;
        }
    }
}