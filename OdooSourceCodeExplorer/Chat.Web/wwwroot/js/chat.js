// Enter submits the chat form; Shift+Enter inserts a new line. Delegated so it survives re-renders.
document.addEventListener('keydown', e => {
    if (e.key !== 'Enter' || e.shiftKey || e.isComposing) return;
    const input = e.target;
    if (!(input instanceof HTMLTextAreaElement) || !input.matches('[data-enter-submits]')) return;
    e.preventDefault();
    const submit = input.form?.querySelector('button[type=submit]');
    if (submit && !submit.disabled) input.form.requestSubmit(submit);
});

// Shows the newest message: scrolls the history to its end, then keeps the chat card in view.
window.chatScrollToLatest = history => {
    if (!history) return;
    history.scrollTo({ top: history.scrollHeight, behavior: 'smooth' });
    history.lastElementChild?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
};
