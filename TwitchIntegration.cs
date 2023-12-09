using NativeWebSocket;
using OWOGame;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using static OwoSensationBuilderAndTester;

public class TwitchManager : MonoBehaviour
{
    // Website Auth Code Grab
    // Lines 163 & 277 Need your Twitch App ID info after you have registered an app at https://dev.twitch.tv/console/apps
    [System.Serializable]
    public class TokenData
    {
        public string token;
    }

    private readonly ConcurrentQueue<Action> _mainThreadActions = new();

    void Start()
    {
        StartLocalServer();
    }
    void Update()
    {
        while (_mainThreadActions.TryDequeue(out var action))
        {
            action.Invoke();
        }
#if !UNITY_WEBGL || UNITY_EDITOR
        ws?.DispatchMessageQueue();
#endif

    }

    private static bool serverStarted = false;

    void StartLocalServer()
    {
        if (serverStarted) return;

        HttpListener listener = new();
        listener.Prefixes.Add("http://localhost:12345/callback/");
        listener.Prefixes.Add("http://localhost:12345/storeToken/");
        listener.Start();
        listener.BeginGetContext(OnHttpRequestReceived, listener);

        serverStarted = true;
    }

    void OnHttpRequestReceived(IAsyncResult result)
    {
        var listener = (HttpListener)result.AsyncState;
        var context = listener.EndGetContext(result);

        if (context.Request.Url.AbsolutePath == "/storeToken/")
        {
            HandleTokenRequest(context);
            return;
        }

        string responseString = @"
<html>
<body>
<script>
  window.onload = function() {
    const fragment = window.location.hash.substring(1);
    const params = new URLSearchParams(fragment);
    const accessToken = params.get('access_token');
    if (accessToken) {
      fetch('http://localhost:12345/storeToken/', {
        method: 'POST',
        body: JSON.stringify({ token: accessToken }),
        headers: {
          'Content-Type': 'application/json'
        }
      }).then(() => {
        // Close the window after sending the token
        window.close();
      });
    } else {
      // Optionally, close the window even if no token is found
      window.close();
    }
  }
</script>
</body>
</html>";


        byte[] buffer = System.Text.Encoding.UTF8.GetBytes(responseString);
        context.Response.ContentLength64 = buffer.Length;
        var output = context.Response.OutputStream;
        output.Write(buffer, 0, buffer.Length);
        output.Close();

        listener.BeginGetContext(OnHttpRequestReceived, listener);
    }
    private string savedToken;
    void HandleTokenRequest(HttpListenerContext context)
    {
        Stream body = context.Request.InputStream;
        System.Text.Encoding encoding = context.Request.ContentEncoding;
        StreamReader reader = new(body, encoding);

        string data = reader.ReadToEnd();

        TokenData tokenData = JsonUtility.FromJson<TokenData>(data);
        var token = tokenData.token;

        if (!string.IsNullOrEmpty(token))
        {
            _mainThreadActions.Enqueue(() =>
            {
                if (log != null) log.AddEntry("Token received");
                savedToken = token;
                FetchUserData(savedToken);
            });
        }
        else
        {
            _mainThreadActions.Enqueue(() =>
            {
                if (log != null) log.AddEntry("Token retrieval failed");
            });
        }

        byte[] responseBuffer = System.Text.Encoding.UTF8.GetBytes("Token received.");
        context.Response.ContentLength64 = responseBuffer.Length;
        var responseOutput = context.Response.OutputStream;
        responseOutput.Write(responseBuffer, 0, responseBuffer.Length);
        responseOutput.Close();

    }
    private void FetchUserData(string token)
    {
        StartCoroutine(GetUserDataCoroutine(token));
    }
    [System.Serializable]
    public class ResponseData
    {
        public UserData[] data;
    }

    [System.Serializable]
    public class UserData
    {
        public string id;
        public string display_name;
    }
    private int channelIDNumber;
    private IEnumerator GetUserDataCoroutine(string token)
    {
        string url = "https://api.twitch.tv/helix/users";

        using UnityWebRequest www = UnityWebRequest.Get(url);
        www.SetRequestHeader("Client-ID", "PLACEHOLDER"); // Need your Twitch Apps id
        www.SetRequestHeader("Authorization", $"Bearer {token}");

        yield return www.SendWebRequest();
        if (www.result == UnityWebRequest.Result.Success)
        {
            // Parse the response to extract the channel ID
            string responseText = www.downloadHandler.text;
            ResponseData responseData = JsonUtility.FromJson<ResponseData>(responseText);

            if (responseData.data.Length > 0)
            {
                string userID = responseData.data[0].id;
                string userName = responseData.data[0].display_name;
                if (int.TryParse(userID, out int channelIDNum))
                {
                    channelIDNumber = channelIDNum;
                    ConnectToPubSub();
                }
                else
                {
                    Debug.LogError("Failed to parse user ID to an integer.");
                }
                channelInputField.text = userName;
            }
            else
            {
                Debug.LogError("Received empty user data from Twitch.");
            }
        }
        else if (www.result == UnityWebRequest.Result.ConnectionError)
        {
            Debug.LogError("Connection Error: " + www.error);
        }
        else if (www.result == UnityWebRequest.Result.ProtocolError)
        {
            Debug.LogError("Protocol Error: " + www.error);
        }

    }
    // Connecting To Twitch for Auth Grab


    [Serializable]
    public class ChannelMessage
    {
        public string type;
        public MessageDataOuter data;
    }

    [Serializable]
    public class MessageDataOuter
    {
        public string topic;
        public string message; 
    }

    [Serializable]
    public class NestedMessageData
    {
        public string type;
        public NestedData data;
    }

    [Serializable]
    public class NestedData
    {
        public string timestamp;
        public Redemption redemption;
    }

    [Serializable]
    public class Redemption
    {
        public Reward reward;
    }

    [Serializable]
    public class Reward
    {
        public string title;
    }

    [Serializable]
    public class BitsEventMessageData
    {
        public BitsEventData data;
    }

    [Serializable]
    public class BitsEventData
    {
        public int bits_used;
    }

    [SerializeField]
    private TMP_Text channelInputField;
    [SerializeField]
    private TMP_InputField testRedeemInputField;
    [SerializeField]
    private TMP_InputField testBitsInputField;
    private const string PUBSUB_ENDPOINT = "wss://pubsub-edge.twitch.tv";
    private WebSocket ws;
    public DynamicLog log;
    private bool awaitingPong = false;

    public void TestLogging()
    {
        // Debug.Log(Time.deltaTime.ToString());
        
    }
    public void InitiateOAuth()
    {
        string authorizationEndpoint = "https://id.twitch.tv/oauth2/authorize";
        string clientId = "PLACEHOLDER"; // Need your Twitch Apps id
        string redirectUri = "http://localhost:12345/callback/";
        string scopes = "channel:read:redemptions+bits:read";  // Check the specific scopes you require.

        string fullUrl = $"{authorizationEndpoint}?client_id={clientId}&redirect_uri={redirectUri}&response_type=token&scope={scopes}";

        Application.OpenURL(fullUrl);
    }

    // Connecting To PubSub to Watch Channel Redeems

    public async void ConnectToPubSub()
    {
        ws = new WebSocket(PUBSUB_ENDPOINT);


        ws.OnOpen += HandleOpen;
        ws.OnMessage += HandleMessage;
        ws.OnError += HandleError;
        ws.OnClose += HandleClose;


        await ws.Connect();
    }
    private IEnumerator WebSocketPayload()
    {
        yield return new WaitForSeconds(1);
        try
        {
            // Send the LISTEN payload for channel point redemptions
            string listenChannelPayload = $"{{\"type\":\"LISTEN\",\"nonce\": \"channel\",\"data\":{{\"topics\":[\"channel-points-channel-v1.{channelIDNumber}\"],\"auth_token\":\"{savedToken}\"}}}}";
            ws.SendText(listenChannelPayload);
            Debug.Log("Listen Channel Payload Sent");
        }
        catch (Exception ex)
        {
            // Handle any exceptions that might occur
            Debug.LogError($"Error while sending Channel LISTEN payload: {ex.Message}");
        }
        yield return new WaitForSeconds(1);
        try
        {
            // Send the LISTEN payload for Bits (use yourUserID or equivalent)
            string listenBitPayload = $"{{\"type\":\"LISTEN\",\"nonce\": \"Bits\",\"data\":{{\"topics\":[\"channel-bits-events-v2.{channelIDNumber}\"],\"auth_token\":\"{savedToken}\"}}}}";
            ws.SendText(listenBitPayload);
            Debug.Log("Listen Bits Payload Sent");
        }
        catch (Exception ex)
        {
            // Handle any exceptions that might occur
            Debug.LogError($"Error while sending Bit LISTEN payload: {ex.Message}");
        }
    }


    private void HandleOpen()
    {
        Debug.Log("Connected to Twitch PubSub");
        StartCoroutine(WebSocketPayload());
        StartPingCycle();

    }
    private IEnumerator WebSocketWatcher()
    {
        bool sentonce = false;
        while (ws != null)
        {
            if (ws.State == WebSocketState.Closed && !sentonce)
            {
                sentonce = true;
                Debug.Log("Websocket Has been Closed");
            }
            if (ws.State == WebSocketState.Open && sentonce)
            {
                ebug.Log("Websocket Is Now Open");
                sentonce = false;
            }

            yield return new WaitForSeconds(1);
        }
        Debug.Log("WebSocket is null");
    }



    private void HandleMessage(byte[] bytes)
    {
        var messageStr = System.Text.Encoding.UTF8.GetString(bytes);
        ChannelMessage message = JsonUtility.FromJson<ChannelMessage>(messageStr);
        // Debug.Log(message.type);
        if (message.type == "PONG")
        {
            awaitingPong = false;
            Debug.Log("Pong");
            return;
        }
        if (message.type == "RECONNECT")
        {
            Debug.Log("Reconnect");
            Reconnect();
            return;
        }
        if (message.type == "AUTH_REVOKED")
        {
            Debug.Log("Auth was revoked");
            return;
        }
        if (message.type == "MESSAGE")
        {

            if (message.data.topic.StartsWith("channel-points-channel-v1"))
            {
                NestedMessageData pointsMessage = JsonUtility.FromJson<NestedMessageData>(message.data.message);
                // Process the points message...
                string redemptionTitle = pointsMessage.data.redemption.reward.title;
                Debug.Log($"Received channel-points message with title: {redemptionTitle}");
                // Do Something with {redemptionTitle} in your custom method

            }
            else if (message.data.topic.StartsWith("channel-bits-events-v2"))
            {
                BitsEventMessageData bitsMessage = JsonUtility.FromJson<BitsEventMessageData>(message.data.message);
                // Process the bits message...
                int bitsUsed = bitsMessage.data.bits_used;
                Debug.Log($"Received bits event message with bits used: {bitsUsed}");
                // Do Something with {bitsUsed} in your custom method
            }
            else
            {
                Debug.Log($"Received unknown message with topic: {message.data.topic}");
            }
        }
    }

    private void StartPingCycle()
    {
        StartCoroutine(PingCoroutine());
    }

    private IEnumerator PingCoroutine()
    {
        while (true)
        {
            yield return new WaitForSeconds(240);  // Wait for 4 minutes
            if (!awaitingPong)
            {
                SendPing();
                awaitingPong = true;
                yield return new WaitForSeconds(5);
                if (awaitingPong)
                {
                    Debug.Log("Failed to receive pong");
                    Reconnect();
                }
            }
        }
    }
    private const int INITIAL_RECONNECT_DELAY = 5;  // 5 seconds
    private const int MAX_RECONNECT_DELAY = 300;    // 5 minutes
    private int currentReconnectDelay = INITIAL_RECONNECT_DELAY;

    private void Reconnect()
    {
        Debug.Log("Trying to Reconnect");
        ws.CancelConnection();

        awaitingPong = false;
        Wait before reconnecting
        StartCoroutine(ReconnectAfterDelay());
    }

    private IEnumerator ReconnectAfterDelay()
    {
        yield return new WaitForSeconds(currentReconnectDelay);

        // Connect using initial logic
        ConnectToPubSub();

        // Increase the reconnection delay for next time, if needed
        currentReconnectDelay *= 2;
        if (currentReconnectDelay > MAX_RECONNECT_DELAY)
        {
            currentReconnectDelay = MAX_RECONNECT_DELAY;
        }
    }


    private void SendPing()
    {
        if (ws.State == WebSocketState.Open)
        {
            string pingPayload = "{\"type\":\"PING\"}";
            Debug.Log("Sent Ping");
            ws.SendText(pingPayload);
        }
        else
        {
            Debug.LogWarning("WebSocket is not open.");
        }
    }

    private void HandleClose(WebSocketCloseCode reason)
    {
        string closeMessage = $"Disconnected from Twitch PubSub. Close Code:{reason}";
        _mainThreadActions.Enqueue(() =>
        {
            Debug.Log(closeMessage);
        });
    }

    private async void OnDestroy()
    {
        if (ws != null)
        {
            await ws.Close();
            ws = null;
        }
    }
}
