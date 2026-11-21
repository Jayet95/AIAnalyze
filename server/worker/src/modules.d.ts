// Wrangler bundles .txt files as text modules (its default rules).
declare module "*.txt" {
  const text: string;
  export default text;
}
