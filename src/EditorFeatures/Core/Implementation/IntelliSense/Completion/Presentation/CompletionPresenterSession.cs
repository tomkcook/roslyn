﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Windows.Input;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.Implementation.IntelliSense.Completion.Presentation
{
    internal sealed class CompletionPresenterSession : ForegroundThreadAffinitizedObject, ICompletionPresenterSession
    {
        internal static readonly object Key = new object();

        private readonly ICompletionBroker _completionBroker;
        internal readonly IGlyphService GlyphService;
        private readonly ITextView _textView;
        private readonly ITextBuffer _subjectBuffer;

        public event EventHandler<EventArgs> Dismissed;
        public event EventHandler<CompletionItemEventArgs> ItemCommitted;
        public event EventHandler<CompletionItemEventArgs> ItemSelected;

        private readonly CompletionSet2 _completionSet;

        private ICompletionSession _editorSessionOpt;
        private bool _ignoreSelectionStatusChangedEvent;

        // Now that PresentItemsInternal is called after a delay, editorSessionOpt may be null because
        // it hasn't been shown yet, or because it was already dismissed.  Use isDismissed to track
        // this scenario.
        private bool _isDismissed;

        public CompletionPresenterSession(
            ICompletionBroker completionBroker,
            IGlyphService glyphService,
            ITextView textView,
            ITextBuffer subjectBuffer)
        {
            _completionBroker = completionBroker;
            this.GlyphService = glyphService;
            _textView = textView;
            _subjectBuffer = subjectBuffer;

            _completionSet = new CompletionSet2(this, textView, subjectBuffer);
            _completionSet.SelectionStatusChanged += OnCompletionSetSelectionStatusChanged;
        }

        public void PresentItems(
            ITrackingSpan triggerSpan,
            IList<CompletionItem> completionItems,
            CompletionItem selectedItem,
            CompletionItem presetBuilder,
            bool suggestionMode,
            bool isSoftSelected)
        {
            AssertIsForeground();

            // check if this update is still relevant
            if (_textView.IsClosed || _isDismissed)
            {
                return;
            }

            _completionSet.SetTrackingSpan(triggerSpan);

            _ignoreSelectionStatusChangedEvent = true;
            try
            {
                _completionSet.SetCompletionItems(completionItems, selectedItem, presetBuilder, suggestionMode, isSoftSelected);
            }
            finally
            {
                _ignoreSelectionStatusChangedEvent = false;
            }

            if (_editorSessionOpt == null)
            {
                // We're tracking the caret.  Don't have the editor do it. 
                // Map the span instead of a point to avoid affinity problems.
                _editorSessionOpt = _completionBroker.CreateCompletionSession(
                    _textView,
                    triggerSpan.GetStartTrackingPoint(PointTrackingMode.Negative),
                    trackCaret: false);

                var debugTextView = _textView as IDebuggerTextView;
                if (debugTextView != null && !debugTextView.IsImmediateWindow)
                {
                    debugTextView.HACK_StartCompletionSession(_editorSessionOpt);
                }

                _editorSessionOpt.Dismissed += (s, e) => OnEditorSessionDismissed();

                // So here's the deal.  We cannot create the editor session and give it the right
                // items (even though we know what they are).  Instead, the session will call
                // back into the ICompletionSourceProvider (which is us) to get those values. It
                // will pass itself along with the calls back into ICompletionSourceProvider.
                // So, in order to make that connection work, we add properties to the session
                // so that we can call back into ourselves, get the items and add it to the
                // session.
                _editorSessionOpt.Properties.AddProperty(Key, this);
                _editorSessionOpt.Start();
            }
        }

        private void OnEditorSessionDismissed()
        {
            AssertIsForeground();

            var dismissed = this.Dismissed;
            if (dismissed != null)
            {
                dismissed(this, new EventArgs());
            }
        }

        internal void OnCompletionItemCommitted(CompletionItem completionItem)
        {
            AssertIsForeground();

            var completionItemCommitted = this.ItemCommitted;
            if (completionItemCommitted != null)
            {
                completionItemCommitted(this, new CompletionItemEventArgs(completionItem));
            }
        }

        private void OnCompletionSetSelectionStatusChanged(object sender, ValueChangedEventArgs<CompletionSelectionStatus> eventArgs)
        {
            AssertIsForeground();

            if (_ignoreSelectionStatusChangedEvent || _textView.IsClosed)
            {
                return;
            }

            var completionItem = _completionSet.GetCompletionItem(eventArgs.NewValue.Completion);
            var completionItemSelected = this.ItemSelected;
            if (completionItemSelected != null && completionItem != null)
            {
                completionItemSelected(this, new CompletionItemEventArgs(completionItem));
            }
        }

        internal void AugmentCompletionSession(IList<CompletionSet> completionSets)
        {
            Contract.ThrowIfTrue(completionSets.Contains(_completionSet));
            completionSets.Add(_completionSet);
        }

        public void Dismiss()
        {
            AssertIsForeground();

            _isDismissed = true;
            if (_editorSessionOpt == null)
            {
                // No editor session, nothing to do here.
                return;
            }

            // Slimy bit of nastiness: When we dismiss the presenter session, that will
            // inadvertantly trigger a LostAggregateFocus event for the view. The reason for this
            // is long and ugly, but essentially when the completion presenter gets keyboard
            // focus, it posts a message to surrender focus to the view. However, if the we're
            // stopping the model computation because the presenter was double-clicked, that
            // message is still sitting in the queue. Then, when the session is dimissed below
            // a LostAggregateFocus event gets triggered when the presenter's space reservation
            // manager is popped off the stack. To work around this, we set focus back to the
            // view here.
            Keyboard.Focus(((IWpfTextView)_textView).VisualElement);

            _editorSessionOpt.Dismiss();
            _editorSessionOpt = null;
        }

        private bool ExecuteKeyboardCommand(IntellisenseKeyboardCommand command)
        {
            var target = _editorSessionOpt != null
                ? _editorSessionOpt.Presenter as IIntellisenseCommandTarget
                : null;

            return target != null && target.ExecuteKeyboardCommand(command);
        }

        public void SelectPreviousItem()
        {
            ExecuteKeyboardCommand(IntellisenseKeyboardCommand.Up);
        }

        public void SelectNextItem()
        {
            ExecuteKeyboardCommand(IntellisenseKeyboardCommand.Down);
        }

        public void SelectPreviousPageItem()
        {
            ExecuteKeyboardCommand(IntellisenseKeyboardCommand.PageUp);
        }

        public void SelectNextPageItem()
        {
            ExecuteKeyboardCommand(IntellisenseKeyboardCommand.PageDown);
        }
    }
}
