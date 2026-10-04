export function Panel(props: { title: string }): JSX.Element {
  return <section data-title={props.title}>{props.title}</section>;
}
