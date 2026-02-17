window.daemaskViz = {
    pivotInstance: null,
    graphInstance: null,

    initPivotGrid: function (containerId) {
        const container = document.getElementById(containerId);
        if (!container) return;

        // If an instance already exists, we destroy it before rebuilding
        // to handle Blazor entirely replacing DOM elements during re-renders.
        if (window.daemaskViz.pivotInstance) {
            window.daemaskViz.pivotInstance.destroy();
        }

        // We use a tiny timeout to ensure Blazor has fully painted the DOM elements
        setTimeout(() => {
            window.daemaskViz.pivotInstance = new Isotope(container, {
                itemSelector: '.pivot-card',
                layoutMode: 'fitRows',
                transitionDuration: '0.4s', // The speed of the sorting animation
                stagger: 30 // Creates a slight cascading effect when rendering
            });
        }, 50); 
    },
    
    filterPivotGrid: function(selectedTypes) {
        if (!window.daemaskViz.pivotInstance) return;

        // If nothing is selected, hide everything. Otherwise, format as ".PDF, .Email"
        let filterStr = selectedTypes.length > 0 
            ? selectedTypes.map(t => '.' + t).join(', ') 
            : '.none'; 

        window.daemaskViz.pivotInstance.arrange({ filter: filterStr });
    },
    // Add dotNetHelper as the 3rd parameter
    renderForceGraph: function (containerId, graphData, dotNetHelper) {
        const container = document.getElementById(containerId);
        if (!container) return;

        container.innerHTML = '';

        const colorMap = {
            'PDF': '#e22850', 'Email': '#58a6ff', 
            'Image': '#3fb950', 'Transcript': '#d2a8ff'
        };

        window.daemaskViz.graphInstance = ForceGraph()(container)
            .graphData(graphData)
            .nodeId('id')
            .nodeLabel('name') 
            .nodeColor(node => colorMap[node.group] || '#8b949e') 
            .nodeRelSize(6)    
            .linkColor(() => 'rgba(139, 148, 158, 0.3)') 
            .linkWidth(link => link.weight || 1)
            .d3Force('charge', d3.forceManyBody().strength(-120)) 
            .onNodeClick(node => {
                // 1. Handle the Canvas Physics (Zoom & Center)
                window.daemaskViz.graphInstance.centerAt(node.x, node.y, 1000);
                window.daemaskViz.graphInstance.zoom(8, 2000);
                
                // 2. FIRE ACROSS THE BRIDGE TO C#!
                // We call the exact name of the [JSInvokable] method we created.
                if (dotNetHelper) {
                    dotNetHelper.invokeMethodAsync('OnNodeSelectedFromGraph', node.id);
                }
            });
    }
};