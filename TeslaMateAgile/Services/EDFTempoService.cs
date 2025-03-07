using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using TeslaMateAgile.Data;
using TeslaMateAgile.Data.Options;
using TeslaMateAgile.Services.Interfaces;
using System.Collections.Generic;
using System;

namespace TeslaMateAgile.Services
{
    public class EDFTempoService : IDynamicPriceDataService
    {
        private readonly HttpClient _client;
        private readonly EDFTempoOptions _options;
        private readonly ILogger _logger;
        private readonly TimeZoneInfo _frenchTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");

        public EDFTempoService(HttpClient client, IOptions<EDFTempoOptions> options, ILogger<EDFTempoService> logger)
        {
            _client = client;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<IEnumerable<Price>> GetPriceData(DateTimeOffset from, DateTimeOffset to)
        {
            from = TimeZoneInfo.ConvertTime(from, _frenchTimeZone);
            to = TimeZoneInfo.ConvertTime(to, _frenchTimeZone);

            _logger.LogDebug("EDF : Range - {from} -> {to}", from, to);

            var days = GenerateDaysQuery(from, to);
            var url = $"{_options.BaseUrl}?{days}";

            _logger.LogDebug("EDF : URL: {url}", url);
            var jsonResponse = await _client.GetStringAsync(url);
            _logger.LogDebug("EDF Response: {jsonResponse}", jsonResponse);

            var data = JsonSerializer.Deserialize<List<TempoDay>>(jsonResponse) ?? throw new Exception("Failed to retrieve or deserialize EDF Tempo API response");
            
            foreach (var item in data)
            {
                _logger.LogDebug("EDF : TempoDay - Date: {item.dateJour}, Color: {item.codeJour}", item.dateJour, item.codeJour);
            }

            return GenerateSchedule(data, from, to);
        }

        private static string GenerateDaysQuery(DateTimeOffset from, DateTimeOffset to)
        {
            var query = new List<string>();
            for (var date = from.Date.AddDays(-1); date <= to.Date; date = date.AddDays(1))
            {
                query.Add($"dateJour[]={date:yyyy-MM-dd}");
            }
            return string.Join("&", query);
        }

        private IEnumerable<Price> GenerateSchedule(List<TempoDay> data, DateTimeOffset from, DateTimeOffset to)
        {
            var segments = new List<(TimeSpan Start, TimeSpan End, int DayOffset, int Peak)>
            {
                (TimeSpan.FromHours(0), TimeSpan.FromHours(6), -1, 0),
                (TimeSpan.FromHours(6), TimeSpan.FromHours(22), 0, 1),
                (TimeSpan.FromHours(22), TimeSpan.FromDays(1), 0, 0)
            };

            var prices = new Dictionary<int, decimal>
            {
                { 0, _options.BLUE_HC }, { 1, _options.BLUE_HP },
                { 2, _options.WHITE_HC }, { 3, _options.WHITE_HP },
                { 4, _options.RED_HC }, { 5, _options.RED_HP }
            };

            var schedule = new List<(DateTimeOffset Date, TimeSpan Start, TimeSpan End, int Code)>();
            for (int i = 1; i < data.Count; i++)
            {
                var date = DateTimeOffset.ParseExact(data[i].dateJour, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
                foreach (var (start, end, dayOffset, peak) in segments)
                {
                    int code = data[i + dayOffset].codeJour;
                    schedule.Add((date, start, end, code * 2 + peak));
                }
            }

            return ExtractPriceSchedule(schedule, from, to, prices);
        }

        private IEnumerable<Price> ExtractPriceSchedule(List<(DateTimeOffset Date, TimeSpan Start, TimeSpan End, int Code)> schedule, DateTimeOffset from, DateTimeOffset to, Dictionary<int, decimal> prices)
        {
            var schedList = new List<Price>();
            foreach (var (date, start, end, code) in schedule)
            {
                var startTime = date.Add(start);
                var endTime = date.Add(end);
                if (startTime >= to || endTime <= from) continue;
                schedList.Add(new Price { ValidFrom = startTime.ToUniversalTime(), ValidTo = endTime.ToUniversalTime(), Value = prices[code] });
            }
            return schedList;
        }
    }

    public class TempoDay
    {
        public string dateJour { get; set; }
        public int codeJour { get; set; }
        public string periode { get; set; }
    }
}
