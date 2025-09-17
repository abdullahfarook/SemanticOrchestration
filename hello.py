from llmsherpa.readers import LayoutPDFReader

def read_pdf(path: str) -> str:
    llmsherpa_api_url = "https://readers.llmsherpa.com/api/document/developer/parseDocument?renderFormat=all"
    # pdf_url = "https://arxiv.org/pdf/1910.13461.pdf" # also allowed is a file path e.g. /home/downloads/xyz.pdf
    pdf_reader = LayoutPDFReader(llmsherpa_api_url)
    # doc = pdf_reader.read_pdf(pdf_url)
    return "success"

def greetings(name: str) -> str:
    return f"Hello, {name}!"
res = read_pdf("")
print(res)