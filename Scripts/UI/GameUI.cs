using Godot;
using RPG.Core;
using System;

namespace RPG.UI
{
    public partial class GameUI : Control
    {
        [ExportGroup("Logic References")]
        [Export] public ToolController Controller;
        [Export] public InputHandler InputHandler;

        [ExportGroup("UI Components")]
        [Export] private VBoxContainer _chatHistory;
        [Export] private TextEdit _inputField;
        [Export] private Button _sendButton;
        [Export] private ScrollContainer _scrollContainer;
        
        private RichTextLabel _currentStreamingLabel;

        public override void _Ready()
        {
            if (_sendButton == null || _inputField == null)
            {
                GD.PrintErr("GameUI: UI references are missing! Assign them in Inspector.");
                return;
            }

            _sendButton.Pressed += OnSendPressed;

            if (Controller != null)
            {
                Controller.OnUIUpdate += AppendToCurrentResponse;
                Controller.OnTurnComplete += FinalizeResponse;
            }
        }

        private void OnSendPressed()
        {
            var text = _inputField.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;
            
            AddMessageBubble(text, true);
            _inputField.Text = "";
            
            _currentStreamingLabel = AddMessageBubble("", false);
            
            if (InputHandler != null)
            {
                InputHandler.ProcessInput(text);
            }
            else
            {
                GD.PrintErr("GameUI: InputHandler is not assigned!");
            }
        }
        
        public void OnProviderUpdate(string data)
        {
            GD.Print($"[System]: {data}");
        }
        
        private void AppendToCurrentResponse(string chunk)
        {
            if (_currentStreamingLabel != null)
            {
                _currentStreamingLabel.Text += $"\n {chunk}";
                ScrollToBottom();
            }
        }

        private void FinalizeResponse()
        {
            _currentStreamingLabel = null;
            GD.Print("UI: Turn Finished");
        }

        private RichTextLabel AddMessageBubble(string text, bool isUser)
        {
            var label = new RichTextLabel();
            
            label.SelectionEnabled = true;
            label.ContextMenuEnabled = true;
            label.FocusMode = FocusModeEnum.Click;

            label.FitContent = true;
            label.Text = text;
            label.Modulate = isUser ? new Color(0.6f, 0.8f, 1f) : new Color(0.9f, 0.9f, 0.9f);
            
            var panel = new PanelContainer();
            panel.MouseFilter = MouseFilterEnum.Pass; 

            panel.AddChild(label);
            _chatHistory.AddChild(panel);
            
            return label;
        }

        private async void ScrollToBottom()
        {
            await ToSignal(GetTree(), "process_frame");
            if (_scrollContainer != null)
                _scrollContainer.ScrollVertical = (int)_scrollContainer.GetVScrollBar().MaxValue;
        }
    }
}