/* Capture the rendered plant once. Never replace it with another tab's current plant. */
(function () {
    'use strict';
    const plant = document.querySelector('meta[name="mes-plant-context"]')?.content || '';
    const unsafe = method => !['GET', 'HEAD', 'OPTIONS'].includes((method || 'GET').toUpperCase());
    const sameOrigin = url => new URL(url, window.location.href).origin === window.location.origin;
    const originalFetch = window.fetch;
    window.fetch = function (input, init) {
        const isRequest = typeof Request !== 'undefined' && input instanceof Request;
        const url = isRequest ? input.url : input;
        if (sameOrigin(url) && unsafe(init?.method || (isRequest ? input.method : 'GET'))) {
            const headers = new Headers(init?.headers || (isRequest ? input.headers : undefined));
            if (!headers.has('X-Plant-Context')) headers.set('X-Plant-Context', plant);
            init = Object.assign({}, init, { headers });
        }
        return originalFetch.call(this, input, init);
    };
    function stamp(form) {
        if (!unsafe(form.method) || !sameOrigin(form.action)) return;
        let field = form.querySelector('input[name="__PlantContext"]');
        if (!field) {
            field = document.createElement('input');
            field.type = 'hidden'; field.name = '__PlantContext';
            form.appendChild(field);
        }
        field.value = plant;
    }
    document.querySelectorAll('form').forEach(stamp);
    document.addEventListener('submit', event => {
        if (event.target instanceof HTMLFormElement) stamp(event.target);
    }, true);
    if (window.jQuery) window.jQuery.ajaxPrefilter(function (options, original, xhr) {
        if (sameOrigin(options.url) && unsafe(options.type)) xhr.setRequestHeader('X-Plant-Context', plant);
    });
})();
