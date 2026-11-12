(function () {
    'use strict';

    function parsePanel() {
        if (!window.jQuery || !jQuery.validator || !jQuery.validator.unobtrusive) return;
        const panel = document.getElementById('crudPanelBody');
        if (!panel) return;
        panel.querySelectorAll('form').forEach(f => {
            f.setAttribute('novalidate', 'novalidate');
            jQuery(f).removeData('validator').removeData('unobtrusiveValidation');
        });
        jQuery.validator.unobtrusive.parse(panel);
    }

    function suppressNativeHtml5(root) {
        (root || document).querySelectorAll('form').forEach(f => {
            f.setAttribute('novalidate', 'novalidate');
        });
    }

    document.addEventListener('DOMContentLoaded', function () {
        suppressNativeHtml5(document);
        if (window.jQuery && jQuery.validator && jQuery.validator.unobtrusive) {
            jQuery.validator.unobtrusive.parse(document);
        }

        const panel = document.getElementById('crudPanelBody');
        if (!panel) return;
        new MutationObserver(() => {
            suppressNativeHtml5(panel);
            parsePanel();
        }).observe(panel, { childList: true, subtree: false });
    });

    window.parseModuleValidation = parsePanel;
})();
