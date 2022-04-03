export function init(elem) {
    enableColumnResizing(elem);

    const bodyClickHandler = event => {
        const columnOptionsElement = elem.querySelector('thead .column-options');
        if (columnOptionsElement && event.path.indexOf(columnOptionsElement) < 0) {
            elem.dispatchEvent(new CustomEvent('closecolumnoptions', { bubbles: true }));
        }
    };
    const keyDownHandler = event => {
        const columnOptionsElement = elem.querySelector('thead .column-options');
        if (columnOptionsElement && event.key === "Escape") {
            elem.dispatchEvent(new CustomEvent('closecolumnoptions', { bubbles: true }));
        }
    };

    document.body.addEventListener('click', bodyClickHandler);
    document.body.addEventListener('mousedown', bodyClickHandler); // Otherwise it seems strange that it doesn't go away until you release the mouse button
    document.body.addEventListener('keydown', keyDownHandler);

    return {
        stop: () => {
            document.body.removeEventListener('click', bodyClickHandler);
            document.body.removeEventListener('mousedown', bodyClickHandler);
            document.body.removeEventListener('keydown', keyDownHandler);
        }
    };
}

export function checkColumnOptionsPosition(elem) {
    const colOptions = elem.querySelector('.column-options');
    if (colOptions && colOptions.offsetLeft < 0) {
        colOptions.style.transform = `translateX(${ -1 * colOptions.offsetLeft }px)`;
    }
}

/**
 * @brief Attaches event handlers onto the DOM for the mouse drags 
 *        to resize the table's columns.
 *        
 * @param elem  The DOM element for the HTML table.
 */
function enableColumnResizing(elem)
{
    // Assumes these elements are in one-to-one correspondence
    const dragHandles = elem.querySelectorAll(':scope > thead .column-width-draghandle');
    const cols = elem.querySelectorAll(':scope > colgroup > col');

    for (let i = 0; i < dragHandles.length; ++i)
    {
        const dragHandle = dragHandles[i];
        const col = cols[i];
        const th = dragHandle.parentNode;

        dragHandle.addEventListener('mousedown', evt => {
            evt.preventDefault();
            evt.stopPropagation();
            const startPageX = evt.pageX;
            const originalColumnWidth = th.offsetWidth;
            let updatedColumnWidth = 0;

            function handleMouseMove(evt) {
                evt.preventDefault();
                evt.stopPropagation();
                const nextWidth = originalColumnWidth + evt.pageX - startPageX;
                if (Math.abs(nextWidth - updatedColumnWidth) > 0) {
                    updatedColumnWidth = nextWidth;
                    col.style.width = `${updatedColumnWidth}px`;
                }
            }

            function handleMouseUp() {
                document.body.removeEventListener('mousemove', handleMouseMove);
                document.body.removeEventListener('mouseup', handleMouseUp);
            }

            document.body.addEventListener('mousemove', handleMouseMove);
            document.body.addEventListener('mouseup', handleMouseUp);
        });
    }
}
