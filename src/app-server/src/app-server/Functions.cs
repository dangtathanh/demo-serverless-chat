using System.Runtime.CompilerServices;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;

using Amazon.Runtime;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.ApiGatewayManagementApi;
using Amazon.ApiGatewayManagementApi.Model;
using Newtonsoft.Json;


// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace app_server
{
    public class Functions
    {
        public const string ConnectionIdField = "connectionId";
        public const string ConnectionIpAddress = "connectionIpAddress";
        public const string ConnetionUserAgent = "connetionUserAgent";


        public const string ClientUniqueId = "clientUniqueId";
        public const string Body = "body";
        public const string SentOn = "sentOn";

        public class Message
        {
            public string ClientUniqueId { get; set; }
            public string Body { get; set; }
            public DateTime Sent { get; set; }
        }

        /// <summary>
        /// DynamoDB table used to store the open connection ids. More advanced use cases could store logged on user map to their connection id to implement direct message chatting.
        /// </summary>
        string ConnectionMappingTable { get; }

        string MessageTable { get; }

        /// <summary>
        /// DynamoDB service client used to store and retieve connection information from the ConnectionMappingTable
        /// </summary>
        IAmazonDynamoDB DDBClient { get; }

        /// <summary>
        /// Factory func to create the AmazonApiGatewayManagementApiClient. This is needed to created per endpoint of the a connection. It is a factory to make it easy for tests
        /// to moq the creation.
        /// </summary>
        Func<string, IAmazonApiGatewayManagementApi> ApiGatewayManagementApiClientFactory { get; }


        /// <summary>
        /// Default constructor that Lambda will invoke.
        /// </summary>
        public Functions()
        {
            DDBClient = new AmazonDynamoDBClient();

            // Grab the name of the DynamoDB from the environment variable setup in the CloudFormation template serverless.template
            ConnectionMappingTable = System.Environment.GetEnvironmentVariable("TABLE_NAME");
            MessageTable = System.Environment.GetEnvironmentVariable("MESSAGE_TABLE_NAME");

            this.ApiGatewayManagementApiClientFactory = (Func<string, AmazonApiGatewayManagementApiClient>)((endpoint) => 
            {
                return new AmazonApiGatewayManagementApiClient(new AmazonApiGatewayManagementApiConfig
                {
                    ServiceURL = endpoint
                });
            });
        }

        /// <summary>
        /// Constructor used for testing allow tests to pass in moq versions of the service clients.
        /// </summary>
        /// <param name="ddbClient"></param>
        /// <param name="apiGatewayManagementApiClientFactory"></param>
        /// <param name="connectionMappingTable"></param>
        public Functions(IAmazonDynamoDB ddbClient, Func<string, IAmazonApiGatewayManagementApi> apiGatewayManagementApiClientFactory, string connectionMappingTable)
        {
            this.DDBClient = ddbClient;
            this.ApiGatewayManagementApiClientFactory = apiGatewayManagementApiClientFactory;
            this.ConnectionMappingTable = connectionMappingTable;
        }

        public async Task<APIGatewayProxyResponse> OnConnectHandler(APIGatewayProxyRequest request, ILambdaContext context)
        {
            try
            {
                var connectionId = request.RequestContext.ConnectionId;
                var ipAddress = request.RequestContext.Identity.SourceIp;
                var userAgent = request.RequestContext.Identity.UserAgent;

                context.Logger.LogLine($"ConnectionId: {connectionId}, IpAddress: {ipAddress}, UserAgent: {userAgent}");

                var ddbRequest = new PutItemRequest
                {
                    TableName = ConnectionMappingTable,
                    Item = new Dictionary<string, AttributeValue>
                    {
                        {ConnectionIdField, new AttributeValue{ S = connectionId}},
                        {ConnectionIpAddress, new AttributeValue{ S = ipAddress}},
                        {ConnetionUserAgent, new AttributeValue{ S = userAgent}}
                    }
                };

                await DDBClient.PutItemAsync(ddbRequest);

                return new APIGatewayProxyResponse
                {
                    StatusCode = 200,
                    Body = "Connected."
                };
            }
            catch (Exception e)
            {
                context.Logger.LogLine("Error connecting: " + e.Message);
                context.Logger.LogLine(e.StackTrace);
                return new APIGatewayProxyResponse
                {
                    StatusCode = 500,
                    Body = $"Failed to connect: {e.Message}"
                };
            }
        }
    

        public async Task<APIGatewayProxyResponse> SendMessageHandler(APIGatewayProxyRequest request, ILambdaContext context)
        {
            try
            {
                // Construct the API Gateway endpoint that incoming message will be broadcasted to.
                var domainName = request.RequestContext.DomainName;
                var stage = request.RequestContext.Stage;
                var endpoint = $"https://{domainName}/{stage}";
                context.Logger.LogLine($"API Gateway management endpoint: {endpoint}");

                context.Logger.LogLine($"Client '{request.RequestContext.ConnectionId}' send a message: {request.Body}.");

                // The body will look something like this: {"message":"sendmessage", "data":"What are you doing?"}
                JsonDocument message = JsonDocument.Parse(request.Body);

                // Grab the data from the JSON body which is the message to broadcasted.
                JsonElement dataProperty;
                if (!message.RootElement.TryGetProperty("data", out dataProperty))
                {
                    context.Logger.LogLine("Failed to find data element in JSON document");
                    return new APIGatewayProxyResponse
                    {
                        StatusCode = (int)HttpStatusCode.BadRequest
                    };
                }

                var connectionId = request.RequestContext.ConnectionId;
                string messageBody = JsonConvert.SerializeObject(dataProperty);

                var msg = JsonConvert.DeserializeObject<Message>(messageBody);
                var sentOn = DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                var ddbRequest = new PutItemRequest
                {
                    TableName = MessageTable,
                    Item = new Dictionary<string, AttributeValue>
                    {
                        {ClientUniqueId, new AttributeValue{ S = msg.ClientUniqueId}},
                        {Body, new AttributeValue{ S = msg.Body}},
                        {SentOn, new AttributeValue{ N = Convert.ToInt64(sentOn.TotalSeconds).ToString()}}
                    }
                };
                await DDBClient.PutItemAsync(ddbRequest);

                var stream = new MemoryStream(UTF8Encoding.UTF8.GetBytes(messageBody));

                // List all of the current connections. In a more advanced use case the table could be used to grab a group of connection ids for a chat group.
                var scanRequest = new ScanRequest
                {
                    TableName = ConnectionMappingTable,
                    ProjectionExpression = ConnectionIdField
                };

                var scanResponse = await DDBClient.ScanAsync(scanRequest);

                // Construct the IAmazonApiGatewayManagementApi which will be used to send the message to.
                var apiClient = ApiGatewayManagementApiClientFactory(endpoint);

                // Loop through all of the connections and broadcast the message out to the connections.
                var count = 0;
                foreach (var item in scanResponse.Items)
                {
                    var postConnectionRequest = new PostToConnectionRequest
                    {
                        ConnectionId = item[ConnectionIdField].S,
                        Data = stream
                    };

                    try
                    {
                        context.Logger.LogLine($"Post to connection {count}: {postConnectionRequest.ConnectionId}");
                        stream.Position = 0;
                        await apiClient.PostToConnectionAsync(postConnectionRequest);
                        count++;
                    }
                    catch (AmazonServiceException e)
                    {
                        // API Gateway returns a status of 410 GONE then the connection is no
                        // longer available. If this happens, delete the identifier
                        // from our DynamoDB table.
                        if (e.StatusCode == HttpStatusCode.Gone)
                        {
                            var ddbDeleteRequest = new DeleteItemRequest
                            {
                                TableName = ConnectionMappingTable,
                                Key = new Dictionary<string, AttributeValue>
                                {
                                    {ConnectionIdField, new AttributeValue {S = postConnectionRequest.ConnectionId}}
                                }
                            };

                            context.Logger.LogLine($"Deleting gone connection: {postConnectionRequest.ConnectionId}");
                            await DDBClient.DeleteItemAsync(ddbDeleteRequest);
                        }
                        else
                        {
                            context.Logger.LogLine($"Error posting message to {postConnectionRequest.ConnectionId}: {e.Message}");
                            context.Logger.LogLine(e.StackTrace);
                        }
                    }
                }

                return new APIGatewayProxyResponse
                {
                    StatusCode = (int)HttpStatusCode.OK,
                    Body = "Data sent to " + count + " connection" + (count == 1 ? "" : "s")
                };
            }
            catch (Exception e)
            {
                context.Logger.LogLine("Error disconnecting: " + e.Message);
                context.Logger.LogLine(e.StackTrace);
                return new APIGatewayProxyResponse
                {
                    StatusCode = (int)HttpStatusCode.InternalServerError,
                    Body = $"Failed to send message: {e.Message}"
                };
            }
        }

        public async Task<APIGatewayProxyResponse> OnDisconnectHandler(APIGatewayProxyRequest request, ILambdaContext context)
        {
            try
            {
                var connectionId = request.RequestContext.ConnectionId;
                context.Logger.LogLine($"ConnectionId: {connectionId}");

                var ddbRequest = new DeleteItemRequest
                {
                    TableName = ConnectionMappingTable,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        {ConnectionIdField, new AttributeValue {S = connectionId}}
                    }
                };

                await DDBClient.DeleteItemAsync(ddbRequest);

                return new APIGatewayProxyResponse
                {
                    StatusCode = 200,
                    Body = "Disconnected."
                };
            }
            catch (Exception e)
            {
                context.Logger.LogLine("Error disconnecting: " + e.Message);
                context.Logger.LogLine(e.StackTrace);
                return new APIGatewayProxyResponse
                {
                    StatusCode = 500,
                    Body = $"Failed to disconnect: {e.Message}"
                };
            }
        }
    }
}