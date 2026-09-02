import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'

const editors = new Map()

export function create(id, element, content, ariaLabel) {
  destroy(id)
  const editor = new Editor({
    element,
    extensions: [StarterKit.configure({
      heading: { levels: [1, 2, 3] },
      link: {
        autolink: true,
        defaultProtocol: 'https',
        openOnClick: false,
        HTMLAttributes: { rel: 'noopener noreferrer', target: '_blank' },
      },
    })],
    content: content || '',
    editorProps: {
      attributes: {
        class: 'rich-text-editor-content',
        'aria-label': ariaLabel || 'Description',
        spellcheck: 'true',
      },
    },
  })
  editors.set(id, editor)
  editor.commands.focus('end')
}

export function execute(id, command) {
  const editor = editors.get(id)
  if (!editor) return
  const chain = editor.chain().focus()
  switch (command) {
    case 'bold': chain.toggleBold().run(); break
    case 'italic': chain.toggleItalic().run(); break
    case 'strike': chain.toggleStrike().run(); break
    case 'heading2': chain.toggleHeading({ level: 2 }).run(); break
    case 'bulletList': chain.toggleBulletList().run(); break
    case 'orderedList': chain.toggleOrderedList().run(); break
    case 'blockquote': chain.toggleBlockquote().run(); break
    case 'code': chain.toggleCode().run(); break
    case 'undo': chain.undo().run(); break
    case 'redo': chain.redo().run(); break
  }
}

export function getHtml(id) {
  return editors.get(id)?.getHTML() || ''
}

export function destroy(id) {
  const editor = editors.get(id)
  if (!editor) return
  editor.destroy()
  editors.delete(id)
}
