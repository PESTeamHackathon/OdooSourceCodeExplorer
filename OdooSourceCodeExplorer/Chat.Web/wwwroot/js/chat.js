// Enter submits the chat form; Shift+Enter inserts a new line. Delegated so it survives re-renders.
document.addEventListener('keydown', e => {
    if (e.key !== 'Enter' || e.shiftKey || e.isComposing) return;
    const input = e.target;
    if (!(input instanceof HTMLTextAreaElement) || !input.matches('[data-enter-submits]')) return;
    e.preventDefault();
    const submit = input.form?.querySelector('button[type=submit]');
    if (submit && !submit.disabled) input.form.requestSubmit(submit);
});

// Shows the newest content: scrolls the history to its end, then keeps the conversation box in view.
window.chatScrollToLatest = history => {
    if (!history) return;
    // Scroll the conversation to its very end, so long answers show their last line and details.
    // Scrolling the last message into view instead would jump back to the start of a tall answer.
    history.scrollTo({ top: history.scrollHeight, behavior: 'smooth' });
    // The conversation box is at most half the screen, so this only keeps it visible on the page.
    history.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
};
