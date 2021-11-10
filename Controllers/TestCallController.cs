using CallingBotSample.Bots;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CallingBotSample.Controllers
{
    [Authorize]
    [Route("makeTestCall")]
    [ApiController]
    public class TestCallController : Controller
    {
        private readonly CallingBot bot;
        private TelemetryClient telemetry;

        public TestCallController(CallingBot bot, TelemetryClient telemetry)
        {
            this.bot = bot;
            this.telemetry = telemetry;
        }


        [HttpGet]
        //[Authorize(Policy = "ApiCall")]
        public async Task StartAsync()
        {
            HttpContext.ValidateAppRole("Api.Call");
            telemetry.TrackEvent("CallTestReceived");
            var callResult = await bot.MakeTestCallAsync();
            await BuildResponseAsync(callResult, this.Response);
            var props = new Dictionary<string, string>
            {
                ["success"] = callResult.Success.ToString(),
                ["message"] = callResult.Message
            };
            telemetry.TrackEvent($"CallTestFinished. Success={callResult.Success}", props);
        }

        private async Task BuildResponseAsync(CallResult callResult, HttpResponse response)
        {
            if (callResult.Success)
            {
                response.StatusCode = StatusCodes.Status200OK;
            }
            else if (Regex.Match(callResult.Message.ToLower(), @"\(p\d+s\)").Success)
            {
                response.StatusCode = StatusCodes.Status504GatewayTimeout;
            }
            else
            {
                response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            }

            response.ContentType = "application/json";
            var content = JsonConvert.SerializeObject(callResult);
            await response.WriteAsync(content);
        }
    }
}
