// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using CallingBotSample.Interfaces;
using CallingBotSample.Utility;
using CallingMeetingBot.Extenstions;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Http;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Builder.Teams;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Configuration;
using Microsoft.Graph;
using Microsoft.Graph.Communications.Client.Authentication;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Graph.Communications.Core.Notifications;
using Microsoft.Graph.Communications.Core.Serialization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CallingBotSample.Bots
{
    public class CallingBot : ActivityHandler
    {
        private readonly IConfiguration configuration;
        public IGraphLogger GraphLogger { get; }
        public TelemetryClient Telemetry { get; }
        private IRequestAuthenticationProvider AuthenticationProvider { get; }

        private INotificationProcessor NotificationProcessor { get; }
        private CommsSerializer Serializer { get; }
        private readonly BotOptions options;

        private readonly ICard card;
        private readonly IGraph graph;
        private readonly IGraphServiceClient graphServiceClient;

        private static Dictionary<string, CallState?> CallsState = new();
        private static List<string> CallsSuccessful = new();

        private IBotFrameworkHttpAdapter botAdapter;


        public CallingBot(IBotFrameworkHttpAdapter botAdapter, BotOptions options, IConfiguration configuration, ICard card, IGraph graph, IGraphServiceClient graphServiceClient, IGraphLogger graphLogger, TelemetryClient telemetry)
        {
            this.options = options;
            this.configuration = configuration;
            this.card = card;
            this.graph = graph;
            this.graphServiceClient = graphServiceClient;
            this.GraphLogger = graphLogger;
            Telemetry = telemetry;
            var name = this.GetType().Assembly.GetName().Name;
            this.AuthenticationProvider = new AuthenticationProvider(name, options.AppId, options.AppSecret, graphLogger);

            this.Serializer = new CommsSerializer();
            this.NotificationProcessor = new NotificationProcessor(Serializer);
            this.NotificationProcessor.OnNotificationReceived += this.NotificationProcessor_OnNotificationReceived;

            this.botAdapter = botAdapter;
        }

        public async Task<CallResult> MakeTestCallAsync()
        {
            var result = new CallResult
            {
                Success = false,
                Message = "The meeting join url was not set up in settings"
            };
            var joinUrl = this.configuration["MeetingJoinUrl"];
            if (string.IsNullOrEmpty(joinUrl))
            {
                var onlineMeeting = await graph.CreateOnlineMeetingAsync();
                if (onlineMeeting is OnlineMeeting)
                {
                    joinUrl = ((OnlineMeeting)onlineMeeting).JoinWebUrl;
                }
                else
                {
                    result.Message = $"The meeting could not be created: {onlineMeeting}";
                }
            }

            if (!string.IsNullOrEmpty(joinUrl))
            {
                var statefullCall = await graph.JoinScheduledMeeting(joinUrl);
                if (statefullCall is Call)
                {
                    var call = (Call)statefullCall;
                    CallsState.Add(call.Id, call.State);
                    return await WaitForCallSuccess(call.Id);
                }
                else
                {
                    result.Message = $"Could not join the call: {statefullCall}";
                }
            }
            return result;
        }

        private async Task<CallResult> WaitForCallSuccess(string callId)
        {
            var result = new CallResult
            {
                Success = false
            };
            // Prevent infinite wait if connection cannot be done
            var start = DateTime.Now;
            var waitingTime = TimeSpan.Zero;
            int.TryParse(this.configuration["TimeoutSeconds"], out int timeoutSeconds);
            if (timeoutSeconds == 0) { timeoutSeconds = 8; } // Dynatrace's timeout is 10s
            while (true)
            {
                if (CallsState[callId] == CallState.Established)
                {
                    // The check happens when the call has been stablished and the bot is in the call
                    result.Success = true;
                    result.Message = "Call was successfull";
                    CallsState.Remove(callId);
                    break;
                }
                if (CallsState[callId] == CallState.Terminated)
                {
                    // The check happens when the call has been terminated
                    // The result depends if the call is in the successful list
                    result.Success = CallsSuccessful.Remove(callId);
                    result.Message = "Call was terminated. Success is determined by the Success value";
                    CallsState.Remove(callId);
                    break;
                }
                if (CallsState[callId] == CallState.Establishing)
                {
                    waitingTime = DateTime.Now - start;
                }
                if (waitingTime.TotalSeconds > timeoutSeconds)
                {
                    result.Message = $"Call could not be established (P{timeoutSeconds}S)";
                    break;
                }
                await Task.Delay(100);
            }
            return result;
        }

        /// <summary>
        /// Process "/callback" notifications asyncronously. 
        /// </summary>
        /// <param name="request"></param>
        /// <param name="response"></param>
        /// <returns></returns>
        public async Task ProcessNotificationAsync(
            HttpRequest request,
            HttpResponse response)
        {
            try
            {
                var httpRequest = request.CreateRequestMessage();
                //var content = await httpRequest.Content.ReadAsStringAsync();
                //Debug.WriteLine(content);
                var results = await this.AuthenticationProvider.ValidateInboundRequestAsync(httpRequest).ConfigureAwait(false);
                if (results.IsValid)
                {
                    var httpResponse = await this.NotificationProcessor.ProcessNotificationAsync(httpRequest).ConfigureAwait(false);
                    await httpResponse.CreateHttpResponseAsync(response).ConfigureAwait(false);
                }
                else
                {
                    var httpResponse = httpRequest.CreateResponse(HttpStatusCode.Forbidden);
                    await httpResponse.CreateHttpResponseAsync(response).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await response.WriteAsync(e.ToString()).ConfigureAwait(false);
            }
        }

        protected override async Task OnMembersAddedAsync(IList<ChannelAccount> membersAdded, ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken cancellationToken)
        {
            var credentials = new MicrosoftAppCredentials(this.configuration[Common.Constants.MicrosoftAppIdConfigurationSettingsKey], this.configuration[Common.Constants.MicrosoftAppPasswordConfigurationSettingsKey]);
            ConversationReference conversationReference = null;
            foreach (var member in membersAdded)
            {

                if (member.Id != turnContext.Activity.Recipient.Id)
                {
                    var proactiveMessage = MessageFactory.Attachment(this.card.GetWelcomeCardAttachment());
                    proactiveMessage.TeamsNotifyUser();
                    var conversationParameters = new ConversationParameters
                    {
                        IsGroup = false,
                        Bot = turnContext.Activity.Recipient,
                        Members = new ChannelAccount[] { member },
                        TenantId = turnContext.Activity.Conversation.TenantId
                    };
                    await ((BotFrameworkAdapter)turnContext.Adapter).CreateConversationAsync(
                        turnContext.Activity.TeamsGetChannelId(),
                        turnContext.Activity.ServiceUrl,
                        credentials,
                        conversationParameters,
                        async (t1, c1) =>
                        {
                            conversationReference = t1.Activity.GetConversationReference();
                            await ((BotFrameworkAdapter)turnContext.Adapter).ContinueConversationAsync(
                                configuration[Common.Constants.MicrosoftAppIdConfigurationSettingsKey],
                                conversationReference,
                                async (t2, c2) =>
                                {
                                    await t2.SendActivityAsync(proactiveMessage, c2);
                                },
                                cancellationToken);
                        },
                        cancellationToken);
                }
            }
        }

        protected override async Task OnMessageActivityAsync(ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(turnContext.Activity.Text))
            {
                dynamic value = turnContext.Activity.Value;
                if (value != null)
                {
                    string type = value["type"];
                    type = string.IsNullOrEmpty(type) ? "." : type.ToLower();
                    await SendReponse(turnContext, type, cancellationToken);
                }
            }
            else
            {
                if (turnContext.Activity.Text.Trim().Equals("help"))
                {
                    var proactiveMessage = MessageFactory.Attachment(this.card.GetWelcomeCardAttachment());
                    await turnContext.SendActivityAsync(proactiveMessage, cancellationToken);
                }
                else
                {
                    await SendReponse(turnContext, turnContext.Activity.Text.Trim().ToLower(), cancellationToken);
                }
            }
        }

        private async Task SendReponse(ITurnContext<IMessageActivity> turnContext, string input, CancellationToken cancellationToken)
        {
            switch (input)
            {
                case "createcall":
                    var call = await graph.CreateCallAsync();
                    if (call != null)
                    {
                        await turnContext.SendActivityAsync("Placed a call Successfully.");
                    }
                    break;
                case "transfercall":
                    var sourceCallResponse = await graph.CreateCallAsync();
                    if (sourceCallResponse != null)
                    {
                        await turnContext.SendActivityAsync("Transferring the call!");
                        await graph.TransferCallAsync(sourceCallResponse.Id);
                    }
                    break;
                case "joinscheduledmeeting":
                    var onlineMeeting = await graph.CreateOnlineMeetingAsync() as OnlineMeeting;
                    if (onlineMeeting != null)
                    {
                        var statefullCall = await graph.JoinScheduledMeeting(onlineMeeting.JoinWebUrl) as Call;
                        if (statefullCall != null)
                        {
                            await turnContext.SendActivityAsync($"[Click here to Join the meeting]({onlineMeeting.JoinWebUrl})");
                        }
                    }
                    break;
                case "inviteparticipant":
                    var meeting = await graph.CreateOnlineMeetingAsync() as OnlineMeeting;
                    if (meeting != null)
                    {
                        var statefullCall = await graph.JoinScheduledMeeting(meeting.JoinWebUrl) as Call;
                        if (statefullCall != null)
                        {

                            graph.InviteParticipant(statefullCall.Id);
                            await turnContext.SendActivityAsync("Invited participant successfuly");
                        }
                    }
                    break;
                default:
                    await turnContext.SendActivityAsync("Welcome to bot");
                    break;
            }
        }

        private void NotificationProcessor_OnNotificationReceived(NotificationEventArgs args)
        {
            _ = NotificationProcessor_OnNotificationReceivedAsync(args).ForgetAndLogExceptionAsync(
              this.GraphLogger,
              $"Error processing notification {args.Notification.ResourceUrl} with scenario {args.ScenarioId}");
        }

        private async Task NotificationProcessor_OnNotificationReceivedAsync(NotificationEventArgs args)
        {
            this.GraphLogger.CorrelationId = args.ScenarioId;
            //Debug.WriteLine($"Bot: Notification received: {args.ResourceData.GetType().FullName}");
            if (args.ResourceData is Call call)
            {
                var properties = new Dictionary<string, string>
                {
                    ["ChangeType"] = args.ChangeType.ToString(),
                    ["CallState"] = call.State.ToString()
                };
                Telemetry.TrackEvent("CallCallbackReceived", properties);
                Debug.WriteLine($"Bot: Call resource -> ChangeType: {args.ChangeType} - CallState: {call.State}");
                /*
                ChangeType: Updated - CallState: Establishing -> Placing the call
                ChangeType: Updated - CallState: Established -> Joined the call
                ChangeType: Deleted - CallState: Terminated -> Removed from meeting
                 */
                //await Task.Delay(16000);
                if (CallsState.ContainsKey(call.Id))
                {
                    CallsState[call.Id] = call.State;
                }

                if (args.ChangeType == ChangeType.Created && call.State == CallState.Incoming)
                {
                    await this.BotAnswerIncomingCallAsync(call.Id, args.TenantId, args.ScenarioId).ConfigureAwait(false);
                }
                if (args.ChangeType == ChangeType.Updated && call.State == CallState.Established)
                {
                    // Bot joined the call
                    if (CallsState.ContainsKey(call.Id) && !CallsSuccessful.Contains(call.Id))
                    {
                        // if the bot joined a call that should hang up, wait, hang up and message the user
                        //await ((BotAdapter)this.botAdapter).ContinueConversationAsync(this.options.AppId, conversationReference, HangupCallback, default(CancellationToken));
                        CallsSuccessful.Add(call.Id);
                    }
                    await Task.Delay(5000);
                    await graph.HangUpCallAsync(call);
                }
            }

        }

        private async Task HangupCallback(ITurnContext turnContext, CancellationToken cancellationToken)
        {
            await turnContext.SendActivityAsync("Bot exited the meeting.");
        }

        private async Task BotAnswerIncomingCallAsync(string callId, string tenantId, Guid scenarioId)
        {
            Debug.WriteLine("Bot: Answering incoming call");
            Task answerTask = Task.Run(async () =>
                                await this.graphServiceClient.Communications.Calls[callId].Answer(
                                    callbackUri: new Uri(options.BotBaseUrl, "callback").ToString(),
                                    mediaConfig: new ServiceHostedMediaConfig
                                    {
                                        PreFetchMedia = new List<MediaInfo>()
                                        {
                                            new MediaInfo()
                                            {
                                                Uri = new Uri(options.BotBaseUrl, "audio/speech.wav").ToString(),
                                                ResourceId = Guid.NewGuid().ToString(),
                                            }
                                        }
                                    },
                                    acceptedModalities: new List<Modality> { Modality.Audio }).Request().PostAsync()
                                 );

            await answerTask.ContinueWith(async (antecedent) =>
            {

                if (antecedent.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                {
                    await Task.Delay(5000);
                    await graphServiceClient.Communications.Calls[callId].PlayPrompt(
                       prompts: new List<Microsoft.Graph.Prompt>()
                       {
                           new MediaPrompt
                           {
                               MediaInfo = new MediaInfo
                               {
                                   Uri = new Uri(options.BotBaseUrl, "audio/speech.wav").ToString(),
                                   ResourceId = Guid.NewGuid().ToString(),
                               }
                           }
                       })
                       .Request()
                       .PostAsync();
                }
            }
          );
        }
    }

    public class CallList : ObservableCollection<CallToHangUp>
    {
        public CallList() : base() { }
    }

    public class CallToHangUp
    {
        private string callId;
        private ConversationReference conversationReference;

        public string CallId { get => callId; set => callId = value; }
        public ConversationReference ConversationReference { get => conversationReference; set => conversationReference = value; }
    }

    public class CallResult
    {
        public bool Success;
        public string Message;
    }
}

